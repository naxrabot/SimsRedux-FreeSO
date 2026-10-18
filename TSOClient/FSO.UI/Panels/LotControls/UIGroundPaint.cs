using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using FSO.LotView;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.HIT;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.Client.UI.Model;
using FSO.UI.Panels.LotControls;

namespace FSO.Client.UI.Panels.LotControls
{
    /// <summary>
    /// SimsRedux: the Terrain Paint tool - drag across the ground to paint it dirt, sand, gravel, snow,
    /// dark grass, autumn leaves, mud or stone (parameters[0] = the kind; 0 or Ctrl = back to grass).
    /// The brush covers the corner under the mouse and the ones around it (Shift: just the one).
    /// </summary>
    public class UIGroundPaint : UICustomLotControl
    {
        VMMultitileGroup Cursor;
        VM vm;
        LotView.World World;
        ILotControl Parent;
        private readonly ushort Kind;

        private bool Drawing;
        private Point LastPosition;
        private Dictionary<Point, VMArchitectureCommand> Commands = new Dictionary<Point, VMArchitectureCommand>();
        private bool WasDown;

        public UIGroundPaint(VM vm, LotView.World world, ILotControl parent, List<int> parameters)
        {
            this.vm = vm;
            World = parent.World;
            Parent = parent;
            Kind = (ushort)((parameters != null && parameters.Count > 0) ? parameters[0] : 1);
            Cursor = vm.Context.CreateObjectInstance(0x2F39B7A6, LotTilePos.OUT_OF_WORLD, FSO.LotView.Model.Direction.NORTH, true);
            ((ObjectComponent)Cursor.Objects[0].WorldUI).ForceDynamic = true;
        }

        public override void MouseDown(UpdateState state)
        {
            if (!Drawing)
            {
                HITVM.Get().PlaySoundEvent(UISounds.BuildDragToolDown);
                LastPosition = new Point(-2, -2);
                Drawing = true;
            }
        }

        public override void MouseUp(UpdateState state)
        {
            if (Drawing)
            {
                var cmds = new List<VMArchitectureCommand>(Commands.Values);
                if (cmds.Count > 0 && (Parent.ActiveEntity == null || vm.Context.Architecture.LastTestCost <= Parent.Budget))
                {
                    vm.SendCommand(new VMNetArchitectureCmd { Commands = cmds });
                    HITVM.Get().PlaySoundEvent(UISounds.BuildDragToolPlace);
                }
                else HITVM.Get().PlaySoundEvent(UISounds.BuildDragToolUp);
            }
            Drawing = false;
            Commands.Clear();
        }

        public override void Update(UpdateState state, bool scrolled)
        {
            var tilePos = World.EstTileAtPosWithScroll(new Vector2(MousePosition.X, MousePosition.Y));
            Point cursor = new Point((int)Math.Round(tilePos.X), (int)Math.Round(tilePos.Y));
            bool redraw = false;
            var kind = Modifiers.IsSet(UILotControlModifiers.CTRL) ? (ushort)0 : Kind;

            if (Drawing && cursor != LastPosition)
            {
                redraw = true;
                int r = Modifiers.IsSet(UILotControlModifiers.SHIFT) ? 0 : 1;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var p = new Point(cursor.X + dx, cursor.Y + dy);
                        Commands[p] = new VMArchitectureCommand
                        {
                            Type = VMArchitectureCommandType.GROUND_DOT,
                            x = p.X - 1,
                            y = p.Y - 1,
                            pattern = kind
                        };
                    }
                LastPosition = cursor;
            }

            var cmds = vm.Context.Architecture.Commands;
            cmds.Clear();
            cmds.AddRange(Commands.Values);
            if (cmds.Count > 0)
            {
                if (!WasDown) { vm.Context.Architecture.SignalRedraw(); WasDown = true; }
                if (redraw) vm.Context.Architecture.SignalRedraw();
                var cost = vm.Context.Architecture.LastTestCost;
                if (cost != 0)
                {
                    var disallowed = Parent.ActiveEntity != null && cost > Parent.Budget;
                    state.UIState.TooltipProperties.Show = true;
                    state.UIState.TooltipProperties.Color = disallowed ? Color.DarkRed : Color.Black;
                    state.UIState.TooltipProperties.Opacity = 1;
                    state.UIState.TooltipProperties.Position = new Vector2(MousePosition.X, MousePosition.Y);
                    state.UIState.Tooltip = "$" + cost;
                    state.UIState.TooltipProperties.UpdateDead = false;
                }
                else
                {
                    state.UIState.TooltipProperties.Show = false;
                    state.UIState.TooltipProperties.Opacity = 0;
                }
            }
            else if (WasDown)
            {
                vm.Context.Architecture.Commands.Clear();
                vm.Context.Architecture.SignalTerrainRedraw();
                vm.Context.Architecture.SignalRedraw();
                WasDown = false;
            }

            Cursor.SetVisualPosition(new Vector3(cursor.X, cursor.Y, (World.State.Level - 1) * 2.95f), Direction.NORTH, vm.Context);
        }

        public override void Release()
        {
            Cursor.Delete(vm.Context);
            vm.Context.Architecture.Commands.Clear();
            vm.Context.Architecture.SignalTerrainRedraw();
            vm.Context.Architecture.SignalRedraw();
        }
    }
}
