using System.Collections.Generic;
using System.Threading.Tasks;

namespace MQTTnet.Extensions.ManagedClient
{
    public interface IManagedMqttClientStorage
    {
        Task SaveQueuedMessagesAsync(IList<ManagedMqttApplicationMessage> messages);

        void SaveQueuedMessages(IList<ManagedMqttApplicationMessage> messages);

        Task<IList<ManagedMqttApplicationMessage>> LoadQueuedMessagesAsync();
    }
}
