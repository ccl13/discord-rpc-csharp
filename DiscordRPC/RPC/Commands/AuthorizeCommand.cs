using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiscordRPC.RPC.Payload;
using Newtonsoft.Json;

namespace DiscordRPC.RPC.Commands
{
    internal class AuthorizeCommand : ICommand
    {
        [JsonProperty("scopes", NullValueHandling = NullValueHandling.Include)]
        public IEnumerable<string> Scopes { get; set; }

        [JsonProperty("client_id", NullValueHandling = NullValueHandling.Include)]
        public string ClientId { get; set; }

        [JsonProperty("rpc_token", NullValueHandling = NullValueHandling.Ignore)]
        public string RPCToken { get; set; }

        [JsonProperty("username", NullValueHandling = NullValueHandling.Ignore)]
        public string UserName { get; set; }

        public IPayload PreparePayload(long nonce)
        {
            return new ArgumentPayload(this, nonce)
            {
                Command = Command.Authorize,
            };
        }
    }
}
