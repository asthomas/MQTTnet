using System.Collections.Generic;
using System.Threading.Tasks;

namespace MQTTnet
{
    public interface IApplicationMessagePublisher
    {
<<<<<<< HEAD
        Task PublishAsync(MqttApplicationMessage applicationMessage);
=======
        Task PublishAsync(IEnumerable<MqttApplicationMessage> applicationMessages);
        void Publish(IEnumerable<MqttApplicationMessage> applicationMessages);
>>>>>>> parent of 4c80ab6... Merge remote-tracking branch 'origin/develop' into SyncIO
    }
}
