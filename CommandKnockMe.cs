using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/knockme</c> - knocks the caller down for testing. Deals lethal damage so the
    /// real OnDamagePlayerRequested → EnterKnockdown path is exercised end-to-end.
    /// Permission node: <c>knockdown.test</c>.
    /// </summary>
    public sealed class CommandKnockMe : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "knockme";
        public string Help => "Knocks yourself down for testing.";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "testknock" };
        public List<string> Permissions => new List<string> { "knockdown.test" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            UnturnedPlayer up = (UnturnedPlayer)caller;
            Player player = up.Player;
            if (player == null || player.life == null || player.life.isDead)
            {
                UnturnedChat.Say(caller, "[Knockdown] Cannot knock: invalid state.", Color.red);
                return;
            }

            // Skip the damage pipeline entirely — go straight into knockdown.
            if (Knockdown.Instance == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Plugin not loaded.", Color.red);
                return;
            }
            Knockdown.Instance.ForceKnockdown(player);

            UnturnedChat.Say(caller, "[Knockdown] Test knockdown triggered.", Color.yellow);
        }
    }
}
