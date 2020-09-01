using DiscordRPC.RPC.Payload;

namespace DiscordRPC.RPC.Commands
{
    internal class GetVoiceSettingsCommand : ICommand
    {
        public IPayload PreparePayload(long nonce)
        {
            return new ArgumentPayload(nonce)
            {
                Command = Command.GetVoiceSettings,
            };
        }
    }
}
