using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordRPC.Message
{
    /// <summary>
    /// 
    /// </summary>
    public class AuthenticatedMessage : IMessage
    {
        /// <summary>
        /// The type of message received from discord
        /// </summary>
        public override MessageType Type { get { return MessageType.Authenticated; } }

        /// <summary>
        /// 
        /// </summary>
		[JsonProperty("access_token")]
        public string AccessToken { get; internal set; }
    }
}
