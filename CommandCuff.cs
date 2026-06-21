using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/cuff</c> - aim at a DOWNED player and run this to handcuff them (bound/arrest state),
    /// regardless of their pose. Reliable alternative to using the handcuff item (the item path
    /// relies on a Harmony hook that may not fire on every build). Permission: <c>knockdown.cuff</c>.
    /// </summary>
    public sealed class CommandCuff : IRocketCommand
    {
        private const float Reach = 4f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "cuff";
        public string Help => "Handcuff the downed player you are aiming at.";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "arrest" };
        public List<string> Permissions => new List<string> { "knockdown.cuff" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            Player player = ((UnturnedPlayer)caller).Player;
            if (player == null)
                return;
            if (Knockdown.Instance == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Plugin not loaded.", Color.red);
                return;
            }

            if (Knockdown.TryCuffAimed(player, Reach))
                UnturnedChat.Say(caller, "[Knockdown] Player cuffed.", Color.green);
            else
                UnturnedChat.Say(caller, "[Knockdown] Aim at a downed player within reach (they must be knocked down).", Color.red);
        }
    }

    /// <summary>
    /// <c>/uncuff</c> - aim at a cuffed player and run this to release them.
    /// Permission: <c>knockdown.cuff</c>.
    /// </summary>
    public sealed class CommandUncuff : IRocketCommand
    {
        private const float Reach = 4f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "uncuff";
        public string Help => "Release the cuffed player you are aiming at.";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "unarrest" };
        public List<string> Permissions => new List<string> { "knockdown.cuff" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            Player player = ((UnturnedPlayer)caller).Player;
            if (player == null)
                return;

            if (Knockdown.TryUncuffAimed(player, Reach))
                UnturnedChat.Say(caller, "[Knockdown] Player released.", Color.green);
            else
                UnturnedChat.Say(caller, "[Knockdown] Aim at a cuffed player within reach.", Color.red);
        }
    }
}
