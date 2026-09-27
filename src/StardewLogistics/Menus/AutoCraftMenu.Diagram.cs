using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>Which view of the plan the planner is showing.</summary>
    internal enum PlanView
    {
        /// <summary>The plan as an indented list of steps.</summary>
        Steps,

        /// <summary>The plan as a flowchart, raw materials to finished item.</summary>
        Diagram
    }

    /// <summary>The planner's diagram: the whole plan as a flowchart that can be moved about and zoomed.</summary>
    /// <remarks>
    /// Raw materials stand on the left and the finished item on the right, each step to the right of what it's made
    /// from and centred on it, like a family tree on its side. Cards come in column by column as the diagram opens,
    /// their links draw themselves, and packets flow along the links towards the product; when the plan changes,
    /// cards glide to their new places. Dragging moves it and the scroll wheel zooms around the cursor. Everything
    /// follows the animation speed, and stands still at zero.
    /// </remarks>
    internal partial class AutoCraftMenu
    {
        /*********
        ** Fields
        *********/
        /// <summary>The size of a card, and the space between columns and rows, in the diagram's own units.</summary>
        private const int CardWidth = 236;
        private const int CardHeight = 64;
        private const int ColumnStep = 320;
        private const int RowStep = 84;

        /// <summary>How far the diagram can be zoomed out and in.</summary>
        private const float MinZoom = 0.3f;
        private const float MaxZoom = 2.2f;

        /// <summary>How long a card takes to pop in, and the pause between one column starting and the next, at normal speed.</summary>
        private const double CardInMs = 280;
        private const double ColumnStaggerMs = 110;

        /// <summary>How long a card takes to glide to a new place when the plan changes, at normal speed.</summary>
        private const double CardMoveMs = 320;

        /// <summary>How long a packet takes to travel a link, at normal speed.</summary>
        private const double PacketMs = 1500;

        /// <summary>Which view is showing, and the switch between them.</summary>
        private PlanView View = PlanView.Steps;
        private DateTime ViewChangedAt = DateTime.MinValue;
        private Rectangle ViewHighlightFrom;

        /// <summary>The diagram's cards, laid out for the plan it was last built for.</summary>
        private readonly List<DiagramCard> Cards = new();
        private CraftPlan DiagramPlan;

        /// <summary>Where the diagram is looking, and how close: where it is now, and where it's easing to.</summary>
        private Vector2 Camera;
        private Vector2 CameraTarget;
        private float Zoom = 1f;
        private float ZoomTarget = 1f;

        /// <summary>A drag in progress: where it started, and where the camera was.</summary>
        private bool Dragging;
        private bool Dragged;
        private Point DragStart;
        private Vector2 DragCamera;

        /// <summary>The highlight on the card under the cursor.</summary>
        private readonly HoverScales CardHover = new();


        /*********
        ** Nested types
        *********/
        /// <summary>One step of the plan, placed in the diagram.</summary>
        private sealed class DiagramCard
        {
            public PlanNode Node;
            public string Key;
            public DiagramCard Parent;
            public int Column;
            public Vector2 Position;
            public Vector2 From;
            public DateTime MovedAt = DateTime.MinValue;
            public DateTime AppearAt;
        }


        /*********
        ** Private methods: tabs
        *********/
        /// <summary>The Steps and Diagram tabs, beside the title.</summary>
        private IEnumerable<(Rectangle Bounds, PlanView View)> GetViewTabs()
        {
            int width = 132;
            int x = this.xPositionOnScreen + this.width - 52 - (2 * width) - 8;
            int y = this.yPositionOnScreen + 24;
            yield return (new Rectangle(x, y, width, 44), PlanView.Steps);
            yield return (new Rectangle(x + width + 8, y, width, 44), PlanView.Diagram);
        }

        /// <summary>Changes view, starting its transition.</summary>
        private void SwitchView(PlanView view)
        {
            if (view == this.View)
                return;

            this.ViewHighlightFrom = this.GetViewHighlight();
            this.ViewChangedAt = DateTime.UtcNow;
            this.View = view;
            if (view == PlanView.Diagram)
                this.BuildDiagram(fit: this.Cards.Count == 0, replay: true);
            Game1.playSound("smallSelect");
        }

        /// <summary>Where the active tab's highlight is: gliding from the last tab to this one.</summary>
        private Rectangle GetViewHighlight()
        {
            Rectangle target = this.GetViewTabs().First(tab => tab.View == this.View).Bounds;
            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.ViewChangedAt, 240));
            if (t >= 1f || this.ViewHighlightFrom.IsEmpty)
                return target;

            return new Rectangle(
                (int)MathHelper.Lerp(this.ViewHighlightFrom.X, target.X, t),
                (int)MathHelper.Lerp(this.ViewHighlightFrom.Y, target.Y, t),
                (int)MathHelper.Lerp(this.ViewHighlightFrom.Width, target.Width, t),
                (int)MathHelper.Lerp(this.ViewHighlightFrom.Height, target.Height, t));
        }

        /// <summary>Draws the tabs, the active one's highlight gliding beneath them.</summary>
        private void DrawViewTabs(SpriteBatch b)
        {
            foreach ((Rectangle bounds, PlanView _) in this.GetViewTabs())
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.65f, 3f, drawShadow: false);

            Rectangle highlight = this.GetViewHighlight();
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), highlight.X, highlight.Y, highlight.Width, highlight.Height, Color.White, 3f, drawShadow: false);

            foreach ((Rectangle bounds, PlanView view) in this.GetViewTabs())
            {
                this.Fx.Control(b, bounds, inset: 6);
                string label = this.Translations.Get("auto.view-" + view.ToString().ToLowerInvariant());
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), view == this.View ? Game1.textColor : Game1.textColor * 0.7f);
            }
        }

        /// <summary>Draws whichever view is showing in the plan panel, sliding the new one in after a switch.</summary>
        private void DrawPlanView(SpriteBatch b)
        {
            Rectangle panel = this.GetTreeBounds();
            Rectangle area = new(panel.X - 4, panel.Y - 8, panel.Width + 8, panel.Height + 16);
            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.ViewChangedAt, 240));
            int direction = this.View == PlanView.Diagram ? 1 : -1;

            bool sliding = t < 1f && UiBatch.Push(b, area, new Vector2((1f - t) * 40 * direction, 0));
            try
            {
                if (this.View == PlanView.Diagram)
                    this.DrawDiagram(b);
                else
                    this.DrawTree(b);
            }
            finally
            {
                if (sliding)
                    UiBatch.Pop(b);
            }

            // The window's own panel over it, fading away, so the new view fades up from the background.
            if (t < 1f && UiBatch.Push(b, area, Vector2.Zero))
            {
                try
                {
                    drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White * (1f - t), 1f, drawShadow: false);
                }
                finally
                {
                    UiBatch.Pop(b);
                }
            }
        }


        /*********
        ** Private methods: layout
        *********/
        /// <summary>Lays the plan out as cards, keeping each card that's still there where it was so it can glide to its new place.</summary>
        /// <param name="fit">Whether to frame the whole diagram afterwards.</param>
        /// <param name="replay">Whether every card pops in again, as when the diagram is first opened.</param>
        private void BuildDiagram(bool fit, bool replay = false)
        {
            Dictionary<string, Vector2> previous = this.Cards.ToDictionary(card => card.Key, this.CardPosition);
            this.Cards.Clear();
            this.DiagramPlan = this.Plan;
            if (this.Plan?.Root == null)
                return;

            int deepest = this.Plan.Root.Walk().Max(node => node.Depth);
            int nextLeaf = 0;
            DateTime now = DateTime.UtcNow;

            float Place(PlanNode node, DiagramCard parent, string key)
            {
                DiagramCard card = new() { Node = node, Key = key, Parent = parent, Column = deepest - node.Depth };
                this.Cards.Add(card);

                float y;
                if (node.Children.Count == 0)
                    y = nextLeaf++ * RowStep;
                else
                {
                    List<float> ys = node.Children.Select((child, i) => Place(child, card, $"{key}/{i}:{child.ItemId}")).ToList();
                    y = (ys.First() + ys.Last()) / 2f;
                }

                card.Position = new Vector2(card.Column * ColumnStep, y);
                if (!replay && previous.TryGetValue(key, out Vector2 before))
                {
                    card.From = before;
                    card.MovedAt = now;
                    card.AppearAt = DateTime.MinValue;
                }
                else
                {
                    card.From = card.Position;
                    card.AppearAt = now.AddMilliseconds(card.Column * ColumnStaggerMs / Math.Max(0.01, UiAnimation.SpeedFactor));
                }
                return y;
            }

            Place(this.Plan.Root, null, "root:" + this.Plan.Root.ItemId);

            if (fit)
                this.FitDiagram(immediate: true);
        }

        /// <summary>Frames the whole diagram in the panel.</summary>
        private void FitDiagram(bool immediate)
        {
            if (this.Cards.Count == 0)
                return;

            Rectangle canvas = this.GetCanvas();
            float left = this.Cards.Min(card => card.Position.X);
            float top = this.Cards.Min(card => card.Position.Y);
            float right = this.Cards.Max(card => card.Position.X) + CardWidth;
            float bottom = this.Cards.Max(card => card.Position.Y) + CardHeight;

            float zoom = Math.Min((canvas.Width - 48) / Math.Max(1f, right - left), (canvas.Height - 48) / Math.Max(1f, bottom - top));
            this.ZoomTarget = Math.Clamp(zoom, MinZoom, 1.2f);
            this.CameraTarget = new Vector2((left + right) / 2f, (top + bottom) / 2f);
            if (immediate)
            {
                this.Zoom = this.ZoomTarget;
                this.Camera = this.CameraTarget;
            }
        }

        /// <summary>Where a card is drawn now: gliding from where it was to where it belongs.</summary>
        private Vector2 CardPosition(DiagramCard card)
        {
            float t = UiAnimation.EaseOut(UiAnimation.Progress(card.MovedAt, CardMoveMs));
            return t >= 1f ? card.Position : Vector2.Lerp(card.From, card.Position, t);
        }

        /// <summary>The area the diagram is drawn in.</summary>
        private Rectangle GetCanvas()
        {
            Rectangle panel = this.GetTreeBounds();
            return new Rectangle(panel.X + 4, panel.Y - 4, panel.Width - 8, panel.Height + 8);
        }

        /// <summary>The transform from the diagram's units to the screen.</summary>
        private Matrix DiagramTransform()
        {
            Rectangle canvas = this.GetCanvas();
            return Matrix.CreateTranslation(-this.Camera.X, -this.Camera.Y, 0)
                * Matrix.CreateScale(this.Zoom, this.Zoom, 1)
                * Matrix.CreateTranslation(canvas.Center.X, canvas.Center.Y, 0);
        }

        /// <summary>Where a point on screen falls in the diagram.</summary>
        private Vector2 ScreenToDiagram(int x, int y, Vector2 camera, float zoom)
        {
            Rectangle canvas = this.GetCanvas();
            return new Vector2(((x - canvas.Center.X) / zoom) + camera.X, ((y - canvas.Center.Y) / zoom) + camera.Y);
        }

        /// <summary>The card under a point on screen, if any.</summary>
        private DiagramCard GetCardAt(int x, int y)
        {
            if (!this.GetCanvas().Contains(x, y))
                return null;

            Vector2 point = this.ScreenToDiagram(x, y, this.Camera, this.Zoom);
            return this.Cards.LastOrDefault(card =>
            {
                Vector2 at = this.CardPosition(card);
                return point.X >= at.X && point.X < at.X + CardWidth && point.Y >= at.Y && point.Y < at.Y + CardHeight;
            });
        }

        /// <summary>A card's bounds on screen, for hanging a dropdown from it.</summary>
        private Rectangle CardOnScreen(DiagramCard card)
        {
            Rectangle canvas = this.GetCanvas();
            Vector2 at = this.CardPosition(card);
            return new Rectangle(
                (int)(((at.X - this.Camera.X) * this.Zoom) + canvas.Center.X),
                (int)(((at.Y - this.Camera.Y) * this.Zoom) + canvas.Center.Y),
                (int)(CardWidth * this.Zoom),
                (int)(CardHeight * this.Zoom));
        }


        /*********
        ** Private methods: input
        *********/
        /// <summary>Handles a press on the diagram: the start of a drag, or of a click on a card.</summary>
        /// <returns>Whether the press was on the diagram.</returns>
        private bool PressDiagram(int x, int y)
        {
            if (this.GetFitButton().Contains(x, y))
            {
                this.FitDiagram(immediate: false);
                Game1.playSound("shwip");
                return true;
            }

            if (!this.GetCanvas().Contains(x, y))
                return false;

            this.Dragging = true;
            this.Dragged = false;
            this.DragStart = new Point(x, y);
            this.DragCamera = this.CameraTarget;
            return true;
        }

        /// <summary>Moves the diagram with the cursor while it's dragged.</summary>
        private void DragDiagram(int x, int y)
        {
            if (!this.Dragging)
                return;

            Vector2 moved = new(x - this.DragStart.X, y - this.DragStart.Y);
            if (moved.Length() > 6)
                this.Dragged = true;
            if (!this.Dragged)
                return;

            // The diagram follows the cursor exactly while held; easing is for letting go and zooming.
            this.CameraTarget = this.Camera = this.DragCamera - (moved / this.Zoom);
        }

        /// <summary>Ends a drag; one that didn't move is a click on whatever card is there.</summary>
        private void ReleaseDiagram(int x, int y)
        {
            if (!this.Dragging)
                return;

            this.Dragging = false;
            if (this.Dragged)
                return;

            DiagramCard card = this.GetCardAt(x, y);
            if (card != null)
                this.ActivateStep(card.Node, this.CardOnScreen(card));
        }

        /// <summary>Zooms the diagram around the cursor.</summary>
        private void ZoomDiagram(int direction)
        {
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            Vector2 under = this.ScreenToDiagram(mouseX, mouseY, this.CameraTarget, this.ZoomTarget);

            this.ZoomTarget = Math.Clamp(this.ZoomTarget * (direction > 0 ? 1.18f : 1 / 1.18f), MinZoom, MaxZoom);

            // Keep the point under the cursor under it.
            Rectangle canvas = this.GetCanvas();
            this.CameraTarget = under - (new Vector2(mouseX - canvas.Center.X, mouseY - canvas.Center.Y) / this.ZoomTarget);
        }

        /// <summary>Eases the camera towards where it's going.</summary>
        private void UpdateDiagram(GameTime time)
        {
            this.CardHover.Update(time);
            if (this.View != PlanView.Diagram)
                return;

            // The plan changed: lay it out again, cards gliding to their new places.
            if (this.DiagramPlan != this.Plan)
                this.BuildDiagram(fit: this.Cards.Count == 0);

            if (!UiAnimation.Enabled)
            {
                this.Camera = this.CameraTarget;
                this.Zoom = this.ZoomTarget;
                return;
            }

            float ease = 1f - (float)Math.Exp(-time.ElapsedGameTime.TotalSeconds * 14 * UiAnimation.SpeedFactor);
            this.Camera = Vector2.Lerp(this.Camera, this.CameraTarget, ease);
            this.Zoom = MathHelper.Lerp(this.Zoom, this.ZoomTarget, ease);
        }

        /// <summary>What to say about the card under the cursor.</summary>
        private string DescribeCard(int x, int y)
        {
            DiagramCard card = this.GetCardAt(x, y);
            this.CardHover.Hover(card != null ? this.Cards.IndexOf(card) : null);
            if (card == null)
                return this.GetFitButton().Contains(x, y) ? this.Translations.Get("auto.diagram-fit-hint") : "";

            PlanNode node = card.Node;
            string text = $"{node.Requested}x {(node.Substitutes.Count > 0 ? CraftPlan.DescribeWithSubstitutes(node, GetName) : node.DisplayName)}\n{this.DescribeStep(node)}";
            if (node.HasChoice)
                text += "\n" + this.Translations.Get(node.CanCraftInstead || node.Kind == PlanStepKind.Craft ? "auto.change-method" : "auto.change-machine");
            return text;
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>The button that frames the whole diagram.</summary>
        private Rectangle GetFitButton()
        {
            Rectangle canvas = this.GetCanvas();
            return new Rectangle(canvas.Right - 84, canvas.Y + 10, 72, 40);
        }

        /// <summary>Draws the diagram in the plan panel.</summary>
        private void DrawDiagram(SpriteBatch b)
        {
            Rectangle panel = this.GetTreeBounds();
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), panel.X, panel.Y - 8, panel.Width, panel.Height + 16, Color.White * 0.85f, 1f, drawShadow: false);

            Rectangle canvas = this.GetCanvas();
            if (this.Cards.Count == 0)
                return;

            if (UiBatch.Push(b, canvas, this.DiagramTransform()))
            {
                try
                {
                    this.DrawGrid(b, canvas);
                    foreach (DiagramCard card in this.Cards.Where(card => card.Parent != null))
                        this.DrawLink(b, card);
                    for (int i = 0; i < this.Cards.Count; i++)
                        this.DrawCard(b, this.Cards[i], i);
                }
                finally
                {
                    UiBatch.Pop(b);
                }
            }

            // Screen-space controls over the diagram: framing it, and how to move it.
            Rectangle fit = this.GetFitButton();
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), fit.X, fit.Y, fit.Width, fit.Height, Color.White, 2f, drawShadow: false);
            this.Fx.Control(b, fit);
            string fitLabel = this.Translations.Get("auto.diagram-fit");
            Vector2 fitSize = Game1.smallFont.MeasureString(fitLabel);
            Utility.drawTextWithShadow(b, fitLabel, Game1.smallFont, new Vector2(fit.Center.X - (fitSize.X / 2), fit.Center.Y - (fitSize.Y / 2)), Game1.textColor);

            string hint = this.Translations.Get("auto.diagram-hint");
            b.DrawString(Game1.smallFont, hint, new Vector2(canvas.X + 10, canvas.Bottom - 28), Game1.textColor * 0.5f, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 1f);
        }

        /// <summary>A faint grid of dots behind the diagram, so moving it reads as moving.</summary>
        private void DrawGrid(SpriteBatch b, Rectangle canvas)
        {
            const int spacing = 48;
            Vector2 topLeft = this.ScreenToDiagram(canvas.Left, canvas.Top, this.Camera, this.Zoom);
            Vector2 bottomRight = this.ScreenToDiagram(canvas.Right, canvas.Bottom, this.Camera, this.Zoom);

            int startX = (int)Math.Floor(topLeft.X / spacing) * spacing;
            int startY = (int)Math.Floor(topLeft.Y / spacing) * spacing;
            Color dot = Game1.textColor * 0.08f;
            for (int x = startX; x <= bottomRight.X; x += spacing)
            {
                for (int y = startY; y <= bottomRight.Y; y += spacing)
                    b.Draw(Game1.staminaRect, new Rectangle(x - 1, y - 1, 3, 3), dot);
            }
        }

        /// <summary>Draws the link from a card to the step it goes into, drawing itself in, with packets flowing along it.</summary>
        private void DrawLink(SpriteBatch b, DiagramCard card)
        {
            // The link waits for both ends, then draws from the ingredient to the product.
            DateTime starts = card.AppearAt > card.Parent.AppearAt ? card.AppearAt : card.Parent.AppearAt;
            float drawn = UiAnimation.EaseOut(UiAnimation.Progress(starts.AddMilliseconds(CardInMs * 0.6), CardInMs));
            if (drawn <= 0f)
                return;

            Vector2 from = this.CardPosition(card) + new Vector2(CardWidth, CardHeight / 2f);
            Vector2 to = this.CardPosition(card.Parent) + new Vector2(0, CardHeight / 2f);
            float bend = Math.Max(40, (to.X - from.X) * 0.5f);
            Vector2 c1 = from + new Vector2(bend, 0);
            Vector2 c2 = to - new Vector2(bend, 0);

            Color colour = card.Node.Missing > 0 ? UiTheme.Bad * 0.55f : Game1.textColor * 0.3f;
            const int segments = 24;
            Vector2 last = from;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                if (t > drawn)
                    t = drawn;
                Vector2 point = Bezier(from, c1, c2, to, t);
                DrawSegment(b, last, point, colour, 3f);
                last = point;
                if (t >= drawn)
                    break;
            }

            // Packets, once the link is in: two per link, heading for the product.
            if (drawn >= 1f && UiAnimation.Enabled && card.Node.Missing <= 0)
            {
                double clock = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds * UiAnimation.SpeedFactor;
                for (int k = 0; k < 2; k++)
                {
                    float u = (float)(((clock / PacketMs) + (k * 0.5) + (card.Column * 0.13)) % 1.0);
                    Vector2 at = Bezier(from, c1, c2, to, u);
                    float fade = Math.Min(1f, Math.Min(u, 1f - u) * 6f);
                    b.Draw(Game1.staminaRect, new Rectangle((int)at.X - 3, (int)at.Y - 3, 7, 7), Game1.textColor * (0.75f * fade));
                }
            }
        }

        /// <summary>Draws one card: the item, how much, and how it's supplied.</summary>
        private void DrawCard(SpriteBatch b, DiagramCard card, int index)
        {
            float appear = UiAnimation.Progress(card.AppearAt, CardInMs);
            if (appear <= 0f)
                return;

            float pop = UiAnimation.EaseOutBack(appear);
            float alpha = Math.Min(1f, appear * 1.6f);
            float hover = (this.CardHover.Get(index) - 1f) / (HoverScales.MaxScale - 1f);
            float scale = (0.7f + (0.3f * pop)) * (1f + (0.04f * hover));

            Vector2 at = this.CardPosition(card);
            Rectangle box = UiAnimation.Scale(new Rectangle((int)at.X, (int)at.Y, CardWidth, CardHeight), scale);
            PlanNode node = card.Node;

            // The card, outlined in the "bad" colour where something can't be supplied.
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), box.X, box.Y, box.Width, box.Height, Color.White * alpha, 1f, drawShadow: false);
            if (node.Missing > 0)
                DrawBorder(b, box, UiTheme.Bad * (0.8f * alpha), 3);
            else if (hover > 0)
                DrawBorder(b, box, Game1.textColor * (0.35f * hover * alpha), 2);

            // Icon, name and how it's supplied.
            float inner = box.Height / (float)CardHeight;
            int icon = (int)(40 * inner);
            ItemIcon.Draw(b, GetIcon(node.ItemId, node.RequiredQuality), new Rectangle(box.X + (int)(12 * inner), box.Center.Y - (icon / 2), icon, icon), alpha * (node.Kind == PlanStepKind.Missing ? 0.5f : 1f));

            int textX = box.X + (int)(60 * inner);
            int textWidth = box.Right - textX - (int)(12 * inner) - (node.HasChoice ? (int)(22 * inner) : 0);
            Color text = (node.Kind == PlanStepKind.Missing ? UiTheme.Bad : Game1.textColor) * alpha;
            string label = node.Substitutes.Count > 0 ? CraftPlan.DescribeWithSubstitutes(node, GetName) : node.DisplayName;
            Marquee.Draw(b, $"{node.Requested}x {label}", Game1.smallFont, new Vector2(textX, box.Y + (int)(8 * inner)), textWidth, text);
            Marquee.Draw(b, this.DescribeStep(node), Game1.smallFont, new Vector2(textX, box.Y + (int)(34 * inner)), textWidth, text * 0.7f);

            if (node.HasChoice)
                this.DrawSwapIcon(b, new Rectangle(box.Right - (int)(28 * inner), box.Y + (int)(8 * inner), (int)(18 * inner), (int)(18 * inner)), alpha);
        }

        /// <summary>A point along a cubic curve.</summary>
        private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float u = 1 - t;
            return (u * u * u * a) + (3 * u * u * t * b) + (3 * u * t * t * c) + (t * t * t * d);
        }

        /// <summary>A straight line, as a rotated strip.</summary>
        private static void DrawSegment(SpriteBatch b, Vector2 from, Vector2 to, Color colour, float thickness)
        {
            Vector2 delta = to - from;
            if (delta.LengthSquared() < 0.01f)
                return;
            b.Draw(Game1.staminaRect, from, null, colour, (float)Math.Atan2(delta.Y, delta.X), new Vector2(0, 0.5f), new Vector2(delta.Length() + 0.5f, thickness), SpriteEffects.None, 0f);
        }

        /// <summary>A border just inside a box.</summary>
        private static void DrawBorder(SpriteBatch b, Rectangle box, Color colour, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(box.X + 4, box.Y + 4, box.Width - 8, thickness), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.X + 4, box.Bottom - 4 - thickness, box.Width - 8, thickness), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.X + 4, box.Y + 4, thickness, box.Height - 8), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.Right - 4 - thickness, box.Y + 4, thickness, box.Height - 8), colour);
        }

        /// <summary>Draws the swap icon that marks a step that can be made another way, in the scheme's text colour.</summary>
        private void DrawSwapIcon(SpriteBatch b, Rectangle area, float alpha)
        {
            this.UiIcons ??= Game1.content.Load<Texture2D>(ModIds.UiIconsTexture);
            b.Draw(this.UiIcons, area, new Rectangle(48, 0, 16, 16), Game1.textColor * (0.75f * alpha));
        }

        /// <summary>The mod's UI icon sheet.</summary>
        private Texture2D UiIcons;
    }
}
