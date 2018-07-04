using System;
using System.Collections.Generic;
using System.Linq;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server
{
    public class MqttClientSubscriptionsManager
    {
        private readonly Dictionary<string, MqttQualityOfServiceLevel> _subscriptions = new Dictionary<string, MqttQualityOfServiceLevel>();
        private readonly IMqttServerOptions _options;
        private readonly MqttServer _server;
        private readonly string _clientId;

        // Key is topic, value is hash of subscription regex
        private readonly Dictionary<string, HashSet<string>> _matchingTopics = new Dictionary<string, HashSet<string>>();

        public MqttClientSubscriptionsManager(string clientId, IMqttServerOptions options, MqttServer server)
        {
            _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _server = server;
        }

        public void NewTopicAdded(string topic)
        {
            // If this topic matches any subscription then add it to a hash of matched topics
            if (!_matchingTopics.ContainsKey(topic))
            {
                foreach (string subscription in _subscriptions.Keys)
                {
                    if (MqttTopicFilterComparer.IsMatch(topic, subscription))
                    {
                        if (!_matchingTopics.ContainsKey(topic))
                            _matchingTopics.Add(topic, new HashSet<string>());
                        HashSet<string> matchingSubscriptions = _matchingTopics[topic];
                        if (!matchingSubscriptions.Contains(subscription))
                            matchingSubscriptions.Add(subscription);
                        break;
                    }
                }
            }
        }

        public void SubscriptionAdded(string subscription)
        {
            // If this subscription matches any existing topics then add this subscription to the topic matches
            // Ust ToList to ensure that we don't iterate on the original collection.
            IEnumerable<string> allTopics = _server.GetAllTopics().ToList();
            foreach(string topic in allTopics)
            {
                if (MqttTopicFilterComparer.IsMatch(topic, subscription))
                {
                    if (!_matchingTopics.ContainsKey(topic))
                        _matchingTopics.Add(topic, new HashSet<string>());
                    HashSet<string> matchingSubscriptions = _matchingTopics[topic];
                    if (!matchingSubscriptions.Contains(subscription))
                        matchingSubscriptions.Add(subscription);
                }
            }
        }

        public void SubscriptionRemoved(string subscription)
        {
            // If this subscription matches any topics, remove the subscription from the topic
            foreach (string topic in _matchingTopics.Keys.ToList())
            {
                if (_matchingTopics[topic].Contains(subscription))
                {
                    _matchingTopics[topic].Remove(subscription);
                }
            }
        }

        public MqttClientSubscribeResult Subscribe(MqttSubscribePacket subscribePacket)
        {
            if (subscribePacket == null) throw new ArgumentNullException(nameof(subscribePacket));

            var result = new MqttClientSubscribeResult
            {
                ResponsePacket = new MqttSubAckPacket
                {
                    PacketIdentifier = subscribePacket.PacketIdentifier
                },

                CloseConnection = false
            };

            foreach (var topicFilter in subscribePacket.TopicFilters)
            {
                var interceptorContext = InterceptSubscribe(topicFilter);
                if (!interceptorContext.AcceptSubscription)
                {
                    result.ResponsePacket.SubscribeReturnCodes.Add(MqttSubscribeReturnCode.Failure);
                }
                else
                {
                    result.ResponsePacket.SubscribeReturnCodes.Add(ConvertToMaximumQoS(topicFilter.QualityOfServiceLevel));
                }

                if (interceptorContext.CloseConnection)
                {
                    result.CloseConnection = true;
                }

                if (interceptorContext.AcceptSubscription)
                {
                    lock (_subscriptions)
                    {
                        _subscriptions[topicFilter.Topic] = topicFilter.QualityOfServiceLevel;
			SubscriptionAdded(topicFilter.Topic);
                    }

                    _server.OnClientSubscribedTopic(_clientId, topicFilter);
                }
            }

            return result;
        }

        public MqttUnsubAckPacket Unsubscribe(MqttUnsubscribePacket unsubscribePacket)
        {
            if (unsubscribePacket == null) throw new ArgumentNullException(nameof(unsubscribePacket));

            lock (_subscriptions)
            {
                foreach (var topicFilter in unsubscribePacket.TopicFilters)
                {
                    _subscriptions.Remove(topicFilter);
		    SubscriptionRemoved(topicFilter);
                    _server.OnClientUnsubscribedTopic(_clientId, topicFilter);
                }
            }

            return new MqttUnsubAckPacket
            {
                PacketIdentifier = unsubscribePacket.PacketIdentifier
            };
        }

        public CheckSubscriptionsResult CheckSubscriptionsLinear(string topic, MqttQualityOfServiceLevel qosLevel)
        {
            var qosLevels = new HashSet<MqttQualityOfServiceLevel>();

            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    if (!MqttTopicFilterComparer.IsMatch(topic, subscription.Key))
                    {
                        continue;
                    }

                    qosLevels.Add(subscription.Value);
                }
            }

            if (qosLevels.Count == 0)
            {
                return new CheckSubscriptionsResult
                {
                    IsSubscribed = false
                };
            }

            return CreateSubscriptionResult(qosLevel, qosLevels);
        }

        public CheckSubscriptionsResult CheckSubscriptions(string topic, MqttQualityOfServiceLevel qosLevel)
        {
            Dictionary<string, HashSet<string>> topicToSubscription = _matchingTopics;
            var qosLevels = new HashSet<MqttQualityOfServiceLevel>();

            if (topicToSubscription.ContainsKey(topic))
            {
                HashSet<string> subscriptions = topicToSubscription[topic];
                foreach (string subscription in subscriptions)
                {
                    MqttQualityOfServiceLevel qos = _subscriptions[subscription];
                    qosLevels.Add(qos);
                }
            }

            if (qosLevels.Count == 0)
            {
                return new CheckSubscriptionsResult
                {
                    IsSubscribed = false
                };
            }

            return CreateSubscriptionResult(qosLevel, qosLevels);
        }

        private static MqttSubscribeReturnCode ConvertToMaximumQoS(MqttQualityOfServiceLevel qualityOfServiceLevel)
        {
            switch (qualityOfServiceLevel)
            {
                case MqttQualityOfServiceLevel.AtMostOnce: return MqttSubscribeReturnCode.SuccessMaximumQoS0;
                case MqttQualityOfServiceLevel.AtLeastOnce: return MqttSubscribeReturnCode.SuccessMaximumQoS1;
                case MqttQualityOfServiceLevel.ExactlyOnce: return MqttSubscribeReturnCode.SuccessMaximumQoS2;
                default: return MqttSubscribeReturnCode.Failure;
            }
        }

        private MqttSubscriptionInterceptorContext InterceptSubscribe(TopicFilter topicFilter)
        {
            var interceptorContext = new MqttSubscriptionInterceptorContext(_clientId, topicFilter);
            _options.SubscriptionInterceptor?.Invoke(interceptorContext);
            return interceptorContext;
        }

        private static CheckSubscriptionsResult CreateSubscriptionResult(MqttQualityOfServiceLevel qosLevel, HashSet<MqttQualityOfServiceLevel> subscribedQoSLevels)
        {
            MqttQualityOfServiceLevel effectiveQoS;
            if (subscribedQoSLevels.Contains(qosLevel))
            {
                effectiveQoS = qosLevel;
            }
            else if (subscribedQoSLevels.Count == 1)
            {
                effectiveQoS = subscribedQoSLevels.First();
            }
            else
            {
                effectiveQoS = subscribedQoSLevels.Max();
            }

            return new CheckSubscriptionsResult
            {
                IsSubscribed = true,
                QualityOfServiceLevel = effectiveQoS
            };
        }
    }
}
