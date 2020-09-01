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
    public class AuthorizedMessage : IMessage
    {
        /// <summary>
        /// The type of message received from discord
        /// </summary>
        public override MessageType Type { get { return MessageType.Authorized; } }

        /// <summary>
        /// 
        /// </summary>
		[JsonProperty("code")]
        public string AccessCode { get; internal set; }
    }
}
