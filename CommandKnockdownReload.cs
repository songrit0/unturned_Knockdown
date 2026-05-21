using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/knockdownreload</c> - reloads Knockdown.configuration.xml from disk.
    /// Permission node: <c>knockdown.reload</c> (admins have it via "*").
    /// </summary>
    public sealed class CommandKnockdownReload : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "knockdownreload";
        public string Help => "Reloads the Knockdown plugin configuration.";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "kdreload" };
        public List<string> Permissions => new List<string> { "knockdown.reload" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (Knockdown.Instance == null)
                return;

            Knockdown.Instance.Configuration.Load();
            UnturnedChat.Say(caller, "[Knockdown] Configuration reloaded.", Color.green);
        }
    }
}
