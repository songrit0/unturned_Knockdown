using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/kftest</c> - adds a demo killfeed line. Each call cycles to the next cause category, so
    /// running it ~11 times shows every icon + colour without needing real kills.
    /// Permission node: <c>knockdown.test</c>.
    /// </summary>
    public sealed class CommandKillfeedTest : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "kftest";
        public string Help => "Add a demo killfeed line (cycles every icon/colour on repeat).";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "killfeedtest" };
        public List<string> Permissions => new List<string> { "knockdown.test" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (Knockdown.Instance == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Plugin not loaded.", Color.red);
                return;
            }
            UnturnedPlayer up = (UnturnedPlayer)caller;
            if (up.Player == null) return;
            if (Knockdown.Instance.Configuration.Instance.KillfeedEffectID == 0)
            {
                UnturnedChat.Say(caller, "[Knockdown] KillfeedEffectID is 0 - set it (30024) and restart first.", Color.red);
                return;
            }
            Knockdown.Instance.KillfeedDemo(up.Player);
            UnturnedChat.Say(caller, "[Knockdown] Killfeed demo added - run again for the next icon.", Color.yellow);
        }
    }
}
