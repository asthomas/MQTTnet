using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Channel;
using MQTTnet.Exceptions;
using MQTTnet.Internal;

namespace MQTTnet.Serializer
{
    public static class MqttPacketReader
    {
        public static MqttFixedHeader ReadFixedHeader(IMqttChannel channel)
        {
            // The MQTT fixed header contains 1 byte of flags and at least 1 byte for the remaining data length.
            // So in all cases at least 2 bytes must be read for a complete MQTT packet.
            var buffer = new byte[2];
            var totalBytesRead = 0;

            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = channel.Read(buffer, 0, buffer.Length - totalBytesRead);
                if (bytesRead <= 0)
                {
                    ExceptionHelper.ThrowGracefulSocketClose();
                }

                totalBytesRead += bytesRead;
            }

            var hasRemainingLength = buffer[1] != 0;
            if (!hasRemainingLength)
            {
                return new MqttFixedHeader(buffer[0], 0);
            }

            var bodyLength = ReadBodyLength(channel, buffer[1]);
            return new MqttFixedHeader(buffer[0], bodyLength);
        }

        public static async Task<MqttFixedHeader> ReadFixedHeaderAsync(IMqttChannel channel, CancellationToken cancellationToken)
        {
            // The MQTT fixed header contains 1 byte of flags and at least 1 byte for the remaining data length.
            // So in all cases at least 2 bytes must be read for a complete MQTT packet.
            var buffer = new byte[2];
            var totalBytesRead = 0;

            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = await channel.ReadAsync(buffer, 0, buffer.Length - totalBytesRead, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    ExceptionHelper.ThrowGracefulSocketClose();
                }

                totalBytesRead += bytesRead;
            }

            var hasRemainingLength = buffer[1] != 0;
            if (!hasRemainingLength)
            {
                return new MqttFixedHeader(buffer[0], 0);
            }

            var bodyLength = await ReadBodyLengthAsync(channel, buffer[1], cancellationToken).ConfigureAwait(false);
            return new MqttFixedHeader(buffer[0], bodyLength);
        }

        private static int ReadBodyLength(IMqttChannel channel, byte initialEncodedByte)
        {
            // Alorithm taken from https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/errata01/os/mqtt-v3.1.1-errata01-os-complete.html.
            var multiplier = 128;
            var value = initialEncodedByte & 127;
            int encodedByte = initialEncodedByte;

            while ((encodedByte & 128) != 0)
            {
                encodedByte = ReadByte(channel);

                value += (byte)(encodedByte & 127) * multiplier;
                if (multiplier > 128 * 128 * 128)
                {
                    throw new MqttProtocolViolationException("Remaining length is invalid.");
                }

                multiplier *= 128;
            }

            return value;
        }

        private static async Task<int> ReadBodyLengthAsync(IMqttChannel channel, byte initialEncodedByte, CancellationToken cancellationToken)
        {
            // Alorithm taken from https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/errata01/os/mqtt-v3.1.1-errata01-os-complete.html.
            var multiplier = 128;
            var value = initialEncodedByte & 127;
            int encodedByte = initialEncodedByte;

            while ((encodedByte & 128) != 0)
            {
                encodedByte = await ReadByteAsync(channel, cancellationToken).ConfigureAwait(false);

                value += (byte)(encodedByte & 127) * multiplier;
                if (multiplier > 128 * 128 * 128)
                {
                    throw new MqttProtocolViolationException("Remaining length is invalid.");
                }

                multiplier *= 128;
            }

            return value;
        }

        private static byte ReadByte(IMqttChannel channel)
        {
            var buffer = new byte[1];
            var readCount = channel.Read(buffer, 0, 1);
            if (readCount <= 0)
            {
                ExceptionHelper.ThrowGracefulSocketClose();
            }

            return buffer[0];
        }

        private static async Task<byte> ReadByteAsync(IMqttChannel channel, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var readCount = await channel.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);
            if (readCount <= 0)
            {
                ExceptionHelper.ThrowGracefulSocketClose();
            }

            return buffer[0];
        }
    }
}
