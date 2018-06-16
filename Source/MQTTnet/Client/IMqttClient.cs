using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MQTTnet.Client
{
    public interface IMqttClient : IApplicationMessageReceiver, IApplicationMessagePublisher, IDisposable
    {
        bool IsConnected { get; }

        event EventHandler<MqttClientConnectedEventArgs> Connected;
        event EventHandler<MqttClientDisconnectedEventArgs> Disconnected;

        Task<MqttClientConnectResult> ConnectAsync(IMqttClientOptions options);
        Task DisconnectAsync();

        MqttClientConnectResult Connect(IMqttClientOptions options);
        void Disconnect();

        Task<IList<MqttSubscribeResult>> SubscribeAsync(IEnumerable<TopicFilter> topicFilters);
        Task UnsubscribeAsync(IEnumerable<string> topics);

        IList<MqttSubscribeResult> Subscribe(IEnumerable<TopicFilter> topicFilters);
        void Unsubscribe(IEnumerable<string> topics);
    }
}