using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using System.Collections.Generic;
using System.Linq;

namespace FSO.Common.Rendering.Framework
{
    /// <summary>
    /// A screen used for drawing.
    /// </summary>
    public class GameScreen
    {
        public List<IGraphicsLayer> Layers = new List<IGraphicsLayer>();
        public GraphicsDevice Device;
        public UpdateState State;

        public static Color ClearColor = new Color(0x72, 0x72, 0x72);

        private int touchedFrames;
		private int lastTouchCount;
		private MouseState lastMouseState;
        private Vector2? prevTouchAvg;
        private const int TOUCH_ACCEPT_TIME = 5;

        public GameScreen(GraphicsDevice device)
        {
            this.Device = device;

            State = new UpdateState();
        }

        private static List<char> TextCharacters = new List<char>();
        public static void TextInput(object sender, TextInputEventArgs e)
        {
            TextCharacters.Add(e.Character);
        }

        /// <summary>
        /// Adds a graphical element to this scene.
        /// </summary>
        /// <param name="layer">Element inheriting from IGraphicsLayer.</param>
        public void Add(IGraphicsLayer layer)
        {
            layer.Initialize(Device);
            Layers.Add(layer);
        }

        public void Update(GameTime time, bool hasFocus)
        {
            State.Time = time;
            State.PreviousKeyboardState = State.KeyboardState;
            State.FrameTextInput = TextCharacters;

            var touchMode = FSOEnvironment.SoftwareKeyboard;

            if (touchMode)
            {
                if (FSOEnvironment.SoftwareDepth) State.KeyboardState = new KeyboardState();
                // SimsRedux website: the page reads the fingers itself (already in the game's pixels);
                // elsewhere the engine's touch panel
                var fingers = new List<KeyValuePair<int, Vector2>>();
                if (FSO.Common.Rendering.Framework.IO.WebTouchInput.Enabled)
                    fingers = FSO.Common.Rendering.Framework.IO.WebTouchInput.Take();
                else
                {
                    TouchCollection touches = TouchPanel.GetState();
                    foreach (var touch in touches)
                        fingers.Add(new KeyValuePair<int, Vector2>(touch.Id,
                            new Vector2(touch.Position.X * FSOEnvironment.InputScaleX, touch.Position.Y * FSOEnvironment.InputScaleY)));
                }

                var missing = new HashSet<MultiMouse>(State.MouseStates);
                //relate touches to their last virtual mouse
                foreach (var touch in fingers)
                {
                    var mouse = State.MouseStates.FirstOrDefault(x => x.ID == touch.Key);
                    if (mouse == null)
                    {
                        mouse = new MultiMouse { ID = touch.Key };
                        State.MouseStates.Add(mouse);
                    }
                    missing.Remove(mouse);

                    mouse.MouseState = new MouseState(
                        (int)touch.Value.X, (int)touch.Value.Y, 0,
                        ButtonState.Pressed,
                        ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released
                        );
                }
                
                //if virtual mouses no longer have their touch, they are performing a "mouse up"
                //if the state has mouseovers, we should record the mouse state as being lifted.
                foreach (var miss in missing)
                {
                    if (miss.LastMouseOver == null && miss.LastMouseDown == null)
                    {
                        State.MouseStates.Remove(miss);
                    } else
                    {
                        miss.MouseState = new MouseState(miss.MouseState.X, miss.MouseState.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
                        miss.Dead = true;
                    }
                }

                // SimsRedux (touch controls on the website): a keyboard attached to a tablet still types
                // and works the shortcuts, and the first finger counts as "where the mouse is" for the
                // tools that ask (never as a held button: taps are handled by the touch code above)
                if (!FSOEnvironment.SoftwareDepth)
                    State.KeyboardState = hasFocus ? Keyboard.GetState() : new KeyboardState();
                var first = State.MouseStates.FirstOrDefault(x => !x.Dead) ?? State.MouseStates.FirstOrDefault();
                if (first != null)
                    State.MouseState = new MouseState(first.MouseState.X, first.MouseState.Y, State.MouseState.ScrollWheelValue,
                        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            }
            else
            {
                //single mouse state
                if (hasFocus)
                {
                    State.MouseState = Mouse.GetState();
                    State.KeyboardState = Keyboard.GetState();
                    if (FSOEnvironment.InputScaleX != 1 || FSOEnvironment.InputScaleY != 1)
                    {
                        // SimsRedux website: the page is showing the game shrunk to fit
                        var ms = State.MouseState;
                        State.MouseState = new MouseState((int)(ms.X * FSOEnvironment.InputScaleX), (int)(ms.Y * FSOEnvironment.InputScaleY), ms.ScrollWheelValue,
                            ms.LeftButton, ms.MiddleButton, ms.RightButton, ms.XButton1, ms.XButton2);
                    }
                }
                else
                {
                    State.MouseState = new MouseState();
                    State.KeyboardState = new KeyboardState();
                }

                // SimsRedux: Alt + left click used to be turned into a middle click (for mice without
                // one). Alt is now the free placement / diagonal rotation key, so that's switched off.
                if (false && State.KeyboardState.IsKeyDown(Keys.LeftAlt) && State.MouseState.LeftButton == ButtonState.Pressed)
                {
                    //emulated middle click with alt
                    var ms = State.MouseState;
                    State.MouseState = new MouseState(ms.X, ms.Y, ms.ScrollWheelValue, ButtonState.Released, ButtonState.Pressed, ms.RightButton, ms.XButton1, ms.XButton2);
                }

                if (State.MouseStates.Count == 0)
                {
                    State.MouseStates.Add(new MultiMouse { ID = 1 });
                }

                State.MouseStates[0].MouseState = State.MouseState;
            }


            State.SharedData.Clear();
            State.Update();

            GameThread.DigestUpdate(State);

            foreach (var layer in Layers){
                layer.Update(State);
            }

            TextCharacters.Clear();
        }

        private void TouchStub(UpdateState state)
        {
#if !WEB
            var test = TouchPanel.EnableMouseTouchPoint;
#endif
            TouchCollection touches = TouchPanel.GetState();
			if (touches.Count != lastTouchCount) touchedFrames = 0;
			lastTouchCount = touches.Count;
            if (touches.Count > 0)
            {
				Vector2 avg = new Vector2();
				for (int i = 0; i < touches.Count; i++)
				{
					avg += touches[i].Position;
				}
				avg /= touches.Count;

                if (touchedFrames < TOUCH_ACCEPT_TIME)
                {
                    avg = prevTouchAvg ?? avg;
					state.MouseState = new MouseState(
						(int)avg.X, (int)avg.Y, state.MouseState.ScrollWheelValue,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released
					);
                    touchedFrames++;
                }
                else
                {
                    state.MouseState = new MouseState(
                        (int)avg.X, (int)avg.Y, state.MouseState.ScrollWheelValue,
						(touches.Count > 1) ? ButtonState.Released : ButtonState.Pressed,
                        (touches.Count > 1) ? ButtonState.Pressed : ButtonState.Released,
                        (touches.Count > 1) ? ButtonState.Pressed : ButtonState.Released,
                        ButtonState.Released,
                        ButtonState.Released
                        );
                    prevTouchAvg = avg;

                    state.TouchMode = true;
                }
            }
            else
            {
                prevTouchAvg = null;
                touchedFrames = 0;
				if (state.TouchMode) state.MouseState = new MouseState(
						lastMouseState.X, lastMouseState.Y, state.MouseState.ScrollWheelValue,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released,
						ButtonState.Released
					);
                //state.TouchMode = false;
            }
			lastMouseState = state.MouseState;
        }

        public void Draw(GameTime time)
        {
            lock (Device)
            {
                foreach (var layer in Layers.Reverse<IGraphicsLayer>())
                {
                    layer.PreDraw(Device);
                }
            }

            Device.SetRenderTarget(null);
            Device.BlendState = BlendState.AlphaBlend;
            Device.Clear(ClearColor);
            //Device.RasterizerState.AlphaBlendEnable = true;
            //Device.DepthStencilState.DepthBufferEnable = true;

            lock (Device)
            {
                foreach (var layer in Layers)
                {
                    layer.Draw(Device);
                }
            }
        }
    }
}
