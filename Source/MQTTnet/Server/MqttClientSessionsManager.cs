using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics;
using MQTTnet.Exceptions;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;

using BlockingCollectionType = MQTTnet.Server.BlockingCollectionSlim<MQTTnet.Server.MqttEnqueuedApplicationMessage>;

namespace MQTTnet.Server
{
    class BlockingCollectionSlim<T> : IDisposable
    {
        private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
        private readonly AutoResetEvent _autoResetEvent = new AutoResetEvent(false);
        public int Count { get { return _queue.Count; } }

        public void Add(T item, CancellationToken cancellationToken)
        {
            _queue.Enqueue(item);
            _autoResetEvent.Set();
        }
        public T Take(CancellationToken cancellationToken)
        {
            T item;
            while (!_queue.TryDequeue(out item))
                _autoResetEvent.WaitOne();
            return item;
        }
        public bool TryTake(out T item, TimeSpan patience)
        {
            if (_queue.TryDequeue(out item))
                return true;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < patience)
            {
                if (_queue.TryDequeue(out item))
                    return true;
                var patienceLeft = (patience - stopwatch.Elapsed);
                if (patienceLeft <= TimeSpan.Zero)
                    break;
                else if (patienceLeft < MinWait)
                    // otherwise the while loop will degenerate into a busy loop,
                    // for the last millisecond before patience runs out
                    patienceLeft = MinWait;
                _autoResetEvent.WaitOne(patienceLeft);
            }
            return false;
        }

        public void Dispose()
        {
            _autoResetEvent.Dispose();
        }

        private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(1);
    }

    public class MqttClientSessionsManager : IDisposable
    {
        private readonly BlockingCollectionType _messageQueue = new BlockingCollectionType();

        /// <summary>
        /// manual locking dictionaries is faster than using concurrent dictionary
        /// </summary>
        private readonly Dictionary<string, MqttClientSession> _sessions = new Dictionary<string, MqttClientSession>();

        private readonly CancellationToken _cancellationToken;

        private readonly MqttRetainedMessagesManager _retainedMessagesManager;
        private readonly IMqttServerOptions _options;
        private readonly IMqttNetChildLogger _logger;

        public MqttClientSessionsManager(IMqttServerOptions options, MqttServer server, MqttRetainedMessagesManager retainedMessagesManager, CancellationToken cancellationToken, IMqttNetChildLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _logger = logger.CreateChildLogger(nameof(MqttClientSessionsManager));

            _cancellationToken = cancellationToken;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Server = server ?? throw new ArgumentNullException(nameof(server));
            _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));
            _retainedMessagesManager.NewTopicAdded += _retainedMessagesManager_NewTopicAdded;
        }

        private void _retainedMessagesManager_NewTopicAdded(object sender, string e)
        {
            foreach (MqttClientSession session in _sessions.Values)
            {
                session.NewTopicAdded(e);
            }
        }

        public MqttServer Server { get; }

        public void Start()
        {
            Task.Factory.StartNew(() => ProcessQueuedApplicationMessages(_cancellationToken), _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop()
        {
            lock (_sessions)
            {
                foreach (var session in _sessions)
                {
                    session.Value.Stop(MqttClientDisconnectType.NotClean);
                }

                _sessions.Clear();
            }
        }

        public Task StartSession(IMqttChannelAdapter clientAdapter)
        {
            return Task.Run(() => RunSession(clientAdapter, _cancellationToken), _cancellationToken);
        }

        public Task<IList<IMqttClientSessionStatus>> GetClientStatusAsync()
        {
            var result = new List<IMqttClientSessionStatus>();

            foreach (var session in GetSessions())
            {
                var status = new MqttClientSessionStatus(this, session);
                session.FillStatus(status);

                result.Add(status);
            }

            return Task.FromResult((IList<IMqttClientSessionStatus>)result);
        }

        public IList<IMqttClientSessionStatus> GetClientStatus()
        {
            var result = new List<IMqttClientSessionStatus>();

            foreach (var session in GetSessions())
            {
                var status = new MqttClientSessionStatus(this, session);
                session.FillStatus(status);

                result.Add(status);
            }

            return result;
        }

        public void EnqueueApplicationMessage(MqttClientSession senderClientSession, MqttPublishPacket publishPacket)
        {
            if (publishPacket == null) throw new ArgumentNullException(nameof(publishPacket));

            if (_messageQueue.Count >= _options.MaxPendingMessagesPerClient)
            {
                if (_options.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
                {
                    publishPacket = null;
                }
                else if (_options.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
                {
                    _messageQueue.Take(CancellationToken.None);
                }
            }

            if (publishPacket != null)
            {
                _messageQueue.Add(new MqttEnqueuedApplicationMessage(senderClientSession, publishPacket), _cancellationToken);
            }
        }

        public Task SubscribeAsync(string clientId, IList<TopicFilter> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(clientId, out var session))
                {
                    throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
                }

                return session.SubscribeAsync(topicFilters);
            }
        }

        public Task UnsubscribeAsync(string clientId, IList<string> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(clientId, out var session))
                {
                    throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
                }

                return session.UnsubscribeAsync(topicFilters);
            }
        }

        public void Subscribe(string clientId, IList<TopicFilter> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(clientId, out var session))
                {
                    throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
                }

                session.Subscribe(topicFilters);
            }
        }

        public void Unsubscribe(string clientId, IList<string> topicFilters)
        {
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(clientId, out var session))
                {
                    throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
                }

                session.Unsubscribe(topicFilters);
            }
        }

        public void DeleteSession(string clientId)
        {
            lock (_sessions)
            {
                _sessions.Remove(clientId);
            }
            _logger.Verbose("Session for client '{0}' deleted.", clientId);
        }

        public void Dispose()
        {
            _messageQueue?.Dispose();
        }

        private void ProcessQueuedApplicationMessages(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var enqueuedApplicationMessage = _messageQueue.Take(cancellationToken);
                    var sender = enqueuedApplicationMessage.Sender;
                    var applicationMessage = enqueuedApplicationMessage.PublishPacket.ToApplicationMessage();

                    var interceptorContext = InterceptApplicationMessage(sender, applicationMessage);
                    if (interceptorContext != null)
                    {
                        if (interceptorContext.CloseConnection)
                        {
                            enqueuedApplicationMessage.Sender.Stop(MqttClientDisconnectType.NotClean);
                        }

                        if (interceptorContext.ApplicationMessage == null || !interceptorContext.AcceptPublish)
                        {
                            return;
                        }

                        applicationMessage = interceptorContext.ApplicationMessage;
                    }

                    Server.OnApplicationMessageReceived(sender?.ClientId, applicationMessage);

                    if (Server.IsSync)
                        _retainedMessagesManager.HandleMessage(sender?.ClientId, applicationMessage);
                    else
                        _retainedMessagesManager.HandleMessageAsync(sender?.ClientId, applicationMessage).GetAwaiter().GetResult();

                    foreach (var clientSession in GetSessions())
                    {
                        clientSession.EnqueueApplicationMessage(enqueuedApplicationMessage.Sender, applicationMessage.ToPublishPacket());
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Unhandled exception while processing queued application message.");
                }
            }
        }

        private List<MqttClientSession> GetSessions()
        {
            lock (_sessions)
            {
                return _sessions.Values.ToList();
            }
        }

        private async Task RunSession(IMqttChannelAdapter clientAdapter, CancellationToken cancellationToken)
        {
            var clientId = string.Empty;
            var wasCleanDisconnect = false;

            try
            {
                MqttBasePacket firstPacket = null;
                if (Server.IsSync)
                    firstPacket = clientAdapter.ReceivePacket(_options.DefaultCommunicationTimeout);
                else
                    firstPacket = await clientAdapter.ReceivePacketAsync(_options.DefaultCommunicationTimeout, cancellationToken).ConfigureAwait(false);
                if (firstPacket == null)
                {
                    return;
                }

                if (!(firstPacket is MqttConnectPacket connectPacket))
                {
                    throw new MqttProtocolViolationException("The first packet from a client must be a 'CONNECT' packet [MQTT-3.1.0-1].");
                }

                clientId = connectPacket.ClientId;

                // Switch to the required protocol version before sending any response.
                clientAdapter.PacketSerializer.ProtocolVersion = connectPacket.ProtocolVersion;

                var connectReturnCode = ValidateConnection(connectPacket);
                if (connectReturnCode != MqttConnectReturnCode.ConnectionAccepted)
                {
                    await clientAdapter.SendPacketAsync(
                        new MqttConnAckPacket
                        {
                            ConnectReturnCode = connectReturnCode
                        },
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                var result = PrepareClientSession(connectPacket);
                var clientSession = result.Session;
                var conack = new MqttConnAckPacket
                {
                    ConnectReturnCode = connectReturnCode,
                    IsSessionPresent = result.IsExistingSession
                };
                if (Server.IsSync)
                {
                    clientAdapter.SendPacket(conack);
                }
                else
                {
                    await clientAdapter.SendPacketAsync(conack,
                        cancellationToken).ConfigureAwait(false);
                }

                Server.OnClientConnected(clientId);

                if (Server.IsSync)
                    wasCleanDisconnect = clientSession.Run(connectPacket, clientAdapter);
                else
                    wasCleanDisconnect = await clientSession.RunAsync(connectPacket, clientAdapter).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, exception.Message);
            }
            finally
            {
                try
                {
                    await clientAdapter.DisconnectAsync(_options.DefaultCommunicationTimeout, CancellationToken.None).ConfigureAwait(false);
                    clientAdapter.Dispose();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, exception.Message);
                }

                if (!_options.EnablePersistentSessions)
                {
                    DeleteSession(clientId);
                }

                Server.OnClientDisconnected(clientId, wasCleanDisconnect);
            }
        }

        public void RunSessionSync(IMqttChannelAdapter clientAdapter, CancellationToken cancellationToken)
        {
            var clientId = string.Empty;
            var wasCleanDisconnect = false;

            try
            {
                var firstPacket = clientAdapter.ReceivePacket(_options.DefaultCommunicationTimeout);
                if (firstPacket == null)
                {
                    return;
                }

                if (!(firstPacket is MqttConnectPacket connectPacket))
                {
                    throw new MqttProtocolViolationException("The first packet from a client must be a 'CONNECT' packet [MQTT-3.1.0-1].");
                }

                clientId = connectPacket.ClientId;

                // Switch to the required protocol version before sending any response.
                clientAdapter.PacketSerializer.ProtocolVersion = connectPacket.ProtocolVersion;

                var connectReturnCode = ValidateConnection(connectPacket);
                if (connectReturnCode != MqttConnectReturnCode.ConnectionAccepted)
                {
                    clientAdapter.SendPacket(new MqttConnAckPacket
                        {
                            ConnectReturnCode = connectReturnCode
                        }
                    );

                    return;
                }

                var result = PrepareClientSession(connectPacket);
                var clientSession = result.Session;

                clientAdapter.SendPacket(new MqttConnAckPacket
                    {
                        ConnectReturnCode = connectReturnCode,
                        IsSessionPresent = result.IsExistingSession
                    }
                );

                Server.OnClientConnected(clientId);

                wasCleanDisconnect = clientSession.Run(connectPacket, clientAdapter);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, exception.Message);
            }
            finally
            {
                try
                {
                    clientAdapter.Disconnect(_options.DefaultCommunicationTimeout);
                    clientAdapter.Dispose();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, exception.Message);
                }

                if (!_options.EnablePersistentSessions)
                {
                    DeleteSession(clientId);
                }

                Server.OnClientDisconnected(clientId, wasCleanDisconnect);
            }
        }

	private MqttConnectReturnCode ValidateConnection(MqttConnectPacket connectPacket)
        {
            if (_options.ConnectionValidator == null)
            {
                return MqttConnectReturnCode.ConnectionAccepted;
            }

            var context = new MqttConnectionValidatorContext(
                connectPacket.ClientId,
                connectPacket.Username,
                connectPacket.Password,
                connectPacket.WillMessage);

            _options.ConnectionValidator(context);
            return context.ReturnCode;
        }

        private PrepareClientSessionResult PrepareClientSession(MqttConnectPacket connectPacket)
        {
            lock (_sessions)
            {
                var isSessionPresent = _sessions.TryGetValue(connectPacket.ClientId, out var clientSession);
                if (isSessionPresent)
                {
                    if (connectPacket.CleanSession)
                    {
                        _sessions.Remove(connectPacket.ClientId);

                        clientSession.Stop(MqttClientDisconnectType.Clean);
                        clientSession.Dispose();
                        clientSession = null;

                        _logger.Verbose("Stopped existing session of client '{0}'.", connectPacket.ClientId);
                    }
                    else
                    {
                        _logger.Verbose("Reusing existing session of client '{0}'.", connectPacket.ClientId);
                    }
                }

                var isExistingSession = true;
                if (clientSession == null)
                {
                    isExistingSession = false;

                    clientSession = new MqttClientSession(connectPacket.ClientId, _options, this, _retainedMessagesManager, _logger);
                    _sessions[connectPacket.ClientId] = clientSession;

                    _logger.Verbose("Created a new session for client '{0}'.", connectPacket.ClientId);
                }

                return new PrepareClientSessionResult { IsExistingSession = isExistingSession, Session = clientSession };
            }
        }

        private MqttApplicationMessageInterceptorContext InterceptApplicationMessage(MqttClientSession sender, MqttApplicationMessage applicationMessage)
        {
            var interceptor = _options.ApplicationMessageInterceptor;
            if (interceptor == null)
            {
                return null;
            }

            var interceptorContext = new MqttApplicationMessageInterceptorContext(sender?.ClientId, applicationMessage);
            interceptor(interceptorContext);
            return interceptorContext;
        }
    }
}