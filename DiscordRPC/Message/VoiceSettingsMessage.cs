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
    public class VoiceSettingsMessage : IMessage
    {
        /// <summary>
        /// The type of message received from discord
        /// </summary>
        public override MessageType Type { get { return MessageType.GotVoiceSettings; } }

        /// <summary>
        /// 
        /// </summary>
		[JsonProperty("mute")]
        public bool IsMuted { get; internal set; }

        /// <summary>
        /// 
        /// </summary>
		[JsonProperty("deaf")]
        public bool IsDeafen { get; internal set; }
    }
}
