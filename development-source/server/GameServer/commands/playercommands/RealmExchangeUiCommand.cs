using DOL.GS.PacketHandler;

namespace DOL.GS.Commands
{
    [CmdAttribute("&exchangeui", ePrivLevel.Player, "Realm Exchange window tab", "/exchangeui <browse|listings> <broker object id>")]
    public sealed class RealmExchangeUiCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client?.Player;
            if (player == null || args.Length != 3 || !ushort.TryParse(args[2], out ushort oid))
                return;
            var broker = oid == 0 ? player.ActiveInventoryObject as RealmExchangeBroker
                : player.CurrentRegion?.GetObject(oid) as RealmExchangeBroker;
            if (broker == null || !broker.OpenUiTab(player, args[1]))
                player.Out.SendMessage("The Realm Exchange could not refresh; talk to your nearby broker again.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
