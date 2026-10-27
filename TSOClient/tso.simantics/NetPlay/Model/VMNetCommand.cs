using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.SimAntics.NetPlay.Model.Commands;

namespace FSO.SimAntics.NetPlay.Model
{
    public class VMNetCommand : VMSerializable
    {
        public static Dictionary<VMCommandType, Type> CmdMap = new Dictionary<VMCommandType, Type> {
            { VMCommandType.SimJoin, typeof(VMNetSimJoinCmd) },
            { VMCommandType.Interaction, typeof(VMNetInteractionCmd) },
            { VMCommandType.Architecture, typeof(VMNetArchitectureCmd) },
            { VMCommandType.BuyObject, typeof(VMNetBuyObjectCmd) },
            { VMCommandType.Chat, typeof(VMNetChatCmd) },
            { VMCommandType.BlueprintRestore, typeof(VMBlueprintRestoreCmd) },
            { VMCommandType.SimLeave, typeof(VMNetSimLeaveCmd) },
            { VMCommandType.InteractionCancel, typeof(VMNetInteractionCancelCmd) },
            { VMCommandType.MoveObject, typeof(VMNetMoveObjectCmd) },
            { VMCommandType.DeleteObject, typeof(VMNetDeleteObjectCmd) },
            { VMCommandType.Goto, typeof(VMNetGotoCmd) },
            { VMCommandType.DialogResponse, typeof(VMNetDialogResponseCmd) },
            { VMCommandType.StateSync, typeof(VMStateSyncCmd) },
            { VMCommandType.RequestResync, typeof(VMRequestResyncCmd) },
            { VMCommandType.GenericDialog, typeof(VMGenericDialogCommand) },
            { VMCommandType.AsyncResponse, typeof(VMNetAsyncResponseCmd) },
            { VMCommandType.ChangePermissions, typeof(VMChangePermissionsCmd) },
            { VMCommandType.EODObjEvent, typeof(VMNetEODEventCmd) },
            { VMCommandType.EODMessage, typeof(VMNetEODMessageCmd) },
            { VMCommandType.UpdatePersistState, typeof(VMNetUpdatePersistStateCmd) },
            { VMCommandType.AdjHollowSync, typeof(VMNetAdjHollowSyncCmd) },
            { VMCommandType.SendToInventory, typeof(VMNetSendToInventoryCmd) },
            { VMCommandType.PlaceInventory, typeof(VMNetPlaceInventoryCmd) },
            { VMCommandType.UpdateInventory, typeof(VMNetUpdateInventoryCmd) },
            { VMCommandType.ChangeEnvironment, typeof(VMNetChangeEnvironmentCmd) },
            { VMCommandType.ChangeLotSize, typeof(VMNetChangeLotSizeCmd) },
            { VMCommandType.InteractionResult, typeof(VMNetInteractionResultCmd) },
            { VMCommandType.AsyncPrice, typeof(VMNetAsyncPriceCmd) },
            { VMCommandType.AsyncSale, typeof(VMNetAsyncSaleCmd) },
            { VMCommandType.LockObject, typeof(VMNetLockCmd) },
            { VMCommandType.SkillLock, typeof(VMNetSkillLockCmd) },
            { VMCommandType.SetIgnore, typeof(VMNetSetIgnoreCmd) },
            { VMCommandType.SetRoof, typeof(VMNetSetRoofCmd) },
			{ VMCommandType.SetOutfit, typeof(VMNetSetOutfitCmd) },
            { VMCommandType.TimeoutNotify, typeof(VMNetTimeoutNotifyCmd) },
            { VMCommandType.ChangeControl, typeof(VMNetChangeControlCmd) },
            { VMCommandType.SetTime, typeof(VMNetSetTimeCmd) },
            { VMCommandType.Tuning, typeof(VMNetTuningCmd) },
            { VMCommandType.BatchGraphic, typeof(VMNetBatchGraphicCmd) },
            { VMCommandType.ChatParameters, typeof(VMNetChatParamCmd) },
            { VMCommandType.ChatEditChan, typeof(VMNetChatEditChanCmd) },
            { VMCommandType.Ping, typeof(VMNetPingCmd) },
            { VMCommandType.Upgrade, typeof(VMNetUpgradeCmd) },
            { VMCommandType.Cheat, typeof(VMNetCheatCmd) },
            { VMCommandType.DirectControl, typeof(VMNetDirectControlCommand) },
            { VMCommandType.DirectControlToggle, typeof(VMNetDirectControlToggleCommand) },
            { VMCommandType.SM64Position, typeof(VMNetSM64PositionCmd) },
            { VMCommandType.SM64Event, typeof(VMNetSM64EventCmd) },
            { VMCommandType.SM64AnimData, typeof(VMNetSM64AnimDataCmd) },
            { VMCommandType.SwapObject, typeof(VMNetSwapObjectCmd) }, // SimsRedux
            { VMCommandType.TintObject, typeof(VMNetTintObjectCmd) }, // SimsRedux
            { VMCommandType.ResetSim, typeof(VMNetResetSimCmd) }, // SimsRedux
            { VMCommandType.SetSpeed, typeof(VMNetSetSpeedCmd) }, // SimsRedux
            { VMCommandType.DebugSim, typeof(VMNetDebugSimCmd) }, // SimsRedux
            { VMCommandType.ReduxTownie, typeof(VMNetReduxTownieCmd) }, // SimsRedux
            { VMCommandType.ReduxOutfit, typeof(VMNetReduxOutfitCmd) }, // SimsRedux
            { VMCommandType.ReduxExterminate, typeof(VMNetReduxExterminateCmd) }, // SimsRedux
            { VMCommandType.ReduxLift, typeof(VMNetLiftObjectCmd) }, // SimsRedux
            { VMCommandType.ReduxBurglar, typeof(VMNetReduxBurglarCmd) }, // SimsRedux
            { VMCommandType.ReduxNovel, typeof(VMNetReduxNovelCmd) }, // SimsRedux
            { VMCommandType.ReduxScale, typeof(VMNetScaleObjectCmd) }, // SimsRedux
            { VMCommandType.ReduxPerks, typeof(VMNetReduxPerksCmd) }, // SimsRedux: Level Rewards (Utils/ReduxPerks.cs)
            { VMCommandType.ReduxBed, typeof(VMNetReduxBedCmd) }, // SimsRedux 15.7: Assign Bed (Utils/ReduxBeds.cs)
            { VMCommandType.ReduxPet, typeof(VMNetReduxPetCmd) }, // SimsRedux 17.0: strays, adopting, pets at home
            { VMCommandType.ReduxPushWall, typeof(VMNetReduxPushWallCmd) }, // SimsRedux 16.7: Push Wall (Utils/ReduxPushWall.cs)
            { VMCommandType.ReduxWooHoo, typeof(VMNetReduxWooHooCmd) }, // SimsRedux 16.7: WooHoo (Utils/ReduxWooHoo.cs)
            { VMCommandType.ReduxCook, typeof(VMNetReduxCookCmd) }, // SimsRedux 16.7: cooking menus (Utils/ReduxCooking.cs)
            { VMCommandType.ReduxTraits, typeof(VMNetReduxTraitsCmd) }, // SimsRedux 16.9: traits (Utils/ReduxTraits.cs)
            { VMCommandType.ReduxAttraction, typeof(VMNetReduxAttractionCmd) }, // SimsRedux 16.9: attraction, relationship types (Utils/ReduxAttraction.cs)
            { VMCommandType.ReduxRelationships, typeof(VMNetReduxRelationshipsCmd) }, // SimsRedux 17.0: relationship stages, breakups, weddings (Utils/ReduxRelationships.cs)
            { VMCommandType.ReduxMatch, typeof(VMNetReduxMatchCmd) }, // SimsRedux 17.0: family and matchmaking (Utils/ReduxMatch.cs)
            { VMCommandType.ReduxRomance, typeof(VMNetReduxRomanceCmd) }, // SimsRedux 17.0: romance and closeness (Utils/ReduxRomance.cs)
            { VMCommandType.ReduxPhone, typeof(VMNetReduxPhoneCmd) }, // SimsRedux 17.1: the cellphone out / put away (Utils/ReduxPhone.cs)
            { VMCommandType.ReduxChores, typeof(VMNetReduxChoresCmd) }, // SimsRedux 17.1: the chore board (Utils/ReduxChores.cs)
            { VMCommandType.ReduxMultitask, typeof(VMNetReduxMultitaskCmd) }, // SimsRedux 17.2: multitasking (Utils/ReduxMultitask.cs)
            { VMCommandType.ReduxStory, typeof(VMNetReduxStoryCmd) }, // SimsRedux 17.2: story progression's changes reaching a family's lot (Simitone's ReduxStory.cs)
        };
        public static Dictionary<Type, VMCommandType> ReverseMap = CmdMap.ToDictionary(x => x.Value, x => x.Key);

        public VMCommandType Type;
        public VMNetCommandBodyAbstract Command;

        public VMNetCommand()
        {
        }

        public VMNetCommand(VMNetCommandBodyAbstract cmd)
        {
            SetCommand(cmd);
        }

        public void SetCommand(VMNetCommandBodyAbstract cmd)
        {
            Type = ReverseMap[cmd.GetType()];
            Command = cmd;
        }

        #region VMSerializable Members

        public void SerializeInto(BinaryWriter writer)
        {
            writer.Write((byte)Type);
            Command.SerializeInto(writer);
        }

        public void Deserialize(BinaryReader reader)
        {
            TryDeserialize(reader, true);
        }

        public bool TryDeserialize(BinaryReader reader, bool isClient)
        {
            Type = (VMCommandType)reader.ReadByte();
            Type cmdType = CmdMap[Type];
            Command = (VMNetCommandBodyAbstract)Activator.CreateInstance(cmdType);
            if (Command.AcceptFromClient || isClient)
            {
                Command.Deserialize(reader);
                return true;
            }
            return false;
        }

        #endregion

    }

    public enum VMCommandType : byte
    {
        SimJoin = 0,
        Interaction = 1,
        Architecture = 2,
        BuyObject = 3,
        Chat = 4,
        BlueprintRestore = 5,
        SimLeave = 6,
        InteractionCancel = 7,
        MoveObject = 8,
        DeleteObject = 9,
        Goto = 10,
        DialogResponse = 11,
        StateSync = 12,
        RequestResync = 13,
        GenericDialog = 14,
        AsyncResponse = 15,
        ChangePermissions = 16,
        EODObjEvent = 17,
        EODMessage = 18,
        UpdatePersistState = 19,
        AdjHollowSync = 20,

        //inventory
        SendToInventory = 21,
        PlaceInventory = 22,
        UpdateInventory = 23,

        //housemode
        ChangeEnvironment = 24,
        ChangeLotSize = 25,

        InteractionResult = 26,

        AsyncPrice = 27,
        AsyncSale = 28,
        LockObject = 29,
        SkillLock = 30,
        SetIgnore = 31,
        SetRoof = 32,
        SetOutfit = 33,

        TimeoutNotify = 34,
        ChangeControl = 35,
        SetTime = 36,
        Tuning = 37,

        BatchGraphic = 38,

        ChatParameters = 39,
        ChatEditChan = 40,
        Ping = 41,
        Upgrade = 42,

        //ts1 cheat
        Cheat = 43,
        DirectControl = 44,
        DirectControlToggle = 45,
        SM64Position = 46,
        SM64Event = 47,
        SM64AnimData = 48,

        // SimsRedux
        SwapObject = 49,
        TintObject = 50,
        ResetSim = 51,
        SetSpeed = 52,
        DebugSim = 53,
        ReduxTownie = 54,
        ReduxOutfit = 55,
        ReduxExterminate = 56,
        ReduxLift = 57,
        ReduxBurglar = 58,
        ReduxNovel = 59,
        ReduxScale = 64, // SimsRedux: object size (60-62 left free for other Redux commands)
        ReduxPerks = 63, // SimsRedux: Level Rewards - the lot's perk table, money and gifts (VMNetReduxPerksCmd)
        ReduxBed = 60, // SimsRedux 15.7: a Sim assigns themselves a bed or side (VMNetReduxBedCmd)
        ReduxPet = 70, // SimsRedux 17.0: strays, adopting a pet, pets at home (VMNetReduxPetCmd)
        ReduxPushWall = 71, // SimsRedux 16.7: Build mode's Push Wall (VMNetReduxPushWallCmd, Utils/ReduxPushWall.cs)
        ReduxWooHoo = 72, // SimsRedux 16.7: WooHoo, babies, protection and pills (VMNetReduxWooHooCmd, Utils/ReduxWooHoo.cs)
        ReduxCook = 73, // SimsRedux 16.7: cooking menus - make a chosen dish (VMNetReduxCookCmd, Utils/ReduxCooking.cs)
        ReduxTraits = 74, // SimsRedux 16.9: traits on the lot, trait-only choices (VMNetReduxTraitsCmd, Utils/ReduxTraits.cs)
        ReduxAttraction = 75, // SimsRedux 16.9: attraction, relationship types, jealousy rule (VMNetReduxAttractionCmd, Utils/ReduxAttraction.cs)
        ReduxRelationships = 77, // SimsRedux 17.0: relationship stages, boundaries, breakups, weddings, anniversaries (VMNetReduxRelationshipsCmd, Utils/ReduxRelationships.cs)
        ReduxMatch = 78, // SimsRedux 17.0: turn-ons, closeness, life partners, dates, adoption, moving in (VMNetReduxMatchCmd, Utils/ReduxMatch.cs)
        ReduxRomance = 79, // SimsRedux 17.0: romance and closeness - romantic choices, privacy, Romance skill (VMNetReduxRomanceCmd, Utils/ReduxRomance.cs)
        ReduxPhone = 83, // SimsRedux 17.1: the cellphone out / put away, a phone over the Sim's head (VMNetReduxPhoneCmd, Utils/ReduxPhone.cs)
        ReduxChores = 80, // SimsRedux 17.1: the chore board - chores at set times, pocket money (VMNetReduxChoresCmd, Utils/ReduxChores.cs)
        ReduxMultitask = 84, // SimsRedux 17.2: multitasking - the lot's switch (VMNetReduxMultitaskCmd, Utils/ReduxMultitask.cs)
        ReduxStory = 85, // SimsRedux 17.2: story progression - skills and jobs from while the family was away (VMNetReduxStoryCmd, Simitone's Utils/ReduxStory.cs)
    }
}
