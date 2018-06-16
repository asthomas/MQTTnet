using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Packets;
using MQTTnet.Serializer;

namespace MQTTnet.Adapter
{
    public interface IMqttChannelAdapter : IDisposable
    {
        string Endpoint { get; }

        IMqttPacketSerializer PacketSerializer { get; }

        event EventHandler ReadingPacketStarted;

        event EventHandler ReadingPacketCompleted;

        Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);

        void Connect(TimeSpan timeout);

        Task DisconnectAsync(TimeSpan timeout, CancellationToken cancellationToken);

        void Disconnect(TimeSpan timeout);

        Task SendPacketAsync(TimeSpan timeout, MqttBasePacket packet, CancellationToken cancellationToken);

        Task<MqttBasePacket> ReceivePacketAsync(TimeSpan timeout, CancellationToken cancellationToken);

        void SendPacket(TimeSpan timeout, MqttBasePacket packet);

        MqttBasePacket ReceivePacket(TimeSpan timeout);
    }
}
