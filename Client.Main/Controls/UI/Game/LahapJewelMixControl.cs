#nullable enable
using System;
using System.Globalization;
using Client.Main.Controllers;
using Client.Main.Controls.UI.Game.Common;
using Client.Main.Controls.UI.Game.Inventory;
using Client.Main.Core.Client;
using Client.Main.Core.Utilities;
using Client.Main.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MUnique.OpenMU.Network.Packets.ClientToServer;

namespace Client.Main.Controls.UI.Game
{
    /// <summary>
    /// Lahap's jewel bundle window: combines 10/20/30 single jewels into a pack or unpacks a pack (C1 BC).
    /// The server accepts these requests only while the Lahap NPC dialog is open, so the dialog stays open
    /// while the window is shown and is closed (CloseNpcRequest) together with the window.
    /// </summary>
    public sealed class LahapJewelMixControl : UIControl
    {
        private readonly record struct JewelMix(byte Number, string Name, byte Group, short Single, short Pack);

        // Server JewelMix number -> single jewel (group, number) -> packed item (group 12, number).
        private static readonly JewelMix[] Mixes =
        {
            new(0, "Jewel of Bless", 14, 13, 30),
            new(1, "Jewel of Soul", 14, 14, 31),
            new(2, "Jewel of Life", 14, 16, 136),
            new(3, "Jewel of Creation", 14, 22, 137),
            new(4, "Jewel of Guardian", 14, 31, 138),
            new(5, "Gemstone", 14, 41, 139),
            new(6, "Jewel of Harmony", 14, 42, 140),
            new(7, "Jewel of Chaos", 12, 15, 141),
            new(8, "Lower Refine Stone", 14, 43, 142),
            new(9, "Higher Refine Stone", 14, 44, 143),
        };

        private const byte PackGroup = 12;
        private const uint CombineFeePerTen = 500_000;
        private const uint UnpackFee = 1_000_000;

        // Inventory layout (as on the server): 8 x 8 main grid from slot 12, then extensions of 8 x 4 each.
        private const int FirstInventorySlot = 12;
        private const int GridColumns = 8;
        private const int MainInventorySlots = 64;
        private const int ExtensionSlots = 32;
        private const int MaxExtensions = 4;

        private const int CombineButtons = 3;
        private const int UnpackButton = CombineButtons;

        private const int Pad = 14;
        private const int HeaderHeight = 48;
        private const int WindowWidth = 520;
        private const int ListHeaderHeight = 20;
        private const int RowHeight = 28;
        private const int ButtonHeight = 22;
        private const int BodyY = HeaderHeight + 4;
        private const int BodyHeight = ListHeaderHeight + RowHeight * 10 + 2;
        private const int InfoY = BodyY + BodyHeight + 8;
        private const int LineHeight = 16;
        private const int WindowHeight = InfoY + LineHeight * 4 + 6 + Pad;

        // Column positions (window-local).
        private const int NameX = Pad + 10;
        private const int SinglesX = 182, SinglesWidth = 50;
        private const int PacksX = 236, PacksWidth = 50;
        private const int CombineX = 294, CombineWidth = 44, ButtonGap = 4;
        private const int UnpackX = 442, UnpackWidth = 60;

        private static readonly Rectangle CloseLocal = new(WindowWidth - 34, 12, 22, 22);
        private static readonly Rectangle BodyLocal = new(Pad, BodyY, WindowWidth - Pad * 2, BodyHeight);

        private static LahapJewelMixControl? _instance;

        private readonly int[] _singles = new int[Mixes.Length];
        private readonly int[] _packs = new int[Mixes.Length];
        private readonly byte[] _firstPackSlot = new byte[Mixes.Length];
        private readonly int[] _firstPackPieces = new int[Mixes.Length];
        private int _freeSlots;
        private uint _zen;

        private CharacterState? _state;
        private volatile bool _dirty = true;
        private bool _closeHovered;
        private int _hoveredRow = -1;
        private int _hoveredButton = -1;
        private bool _isDragging;
        private Point _dragOffset;

        private LahapJewelMixControl()
        {
            AutoViewSize = false;
            Interactive = true;
            ControlSize = ViewSize = new Point(WindowWidth, WindowHeight);
            Align = ControlAlign.HorizontalCenter | ControlAlign.VerticalCenter;
            Visible = false;
        }

        public static LahapJewelMixControl Instance => _instance ??= new LahapJewelMixControl();

        public override bool NonDisposable => true;

        // ─────────────────────────────── Visibility ───────────────────────────────

        /// <summary>Shows the window; the Lahap NPC dialog stays open on the server meanwhile.</summary>
        public void ShowWindow()
        {
            AttachState();
            Refresh();
            Visible = true;
            BringToFront();
        }

        /// <summary>Closes the window and ends the Lahap NPC dialog on the server.</summary>
        public void CloseWindow()
        {
            if (!Visible) return;
            Hide();
            var service = MuGame.Network?.GetCharacterService();
            if (service != null) _ = service.SendCloseNpcRequestAsync();
        }

        private void Hide()
        {
            Visible = false;
            _isDragging = false;
            _closeHovered = false;
            _hoveredRow = -1;
            _hoveredButton = -1;
        }

        private Rectangle Translate(Rectangle local)
            => new(DisplayRectangle.X + local.X, DisplayRectangle.Y + local.Y, local.Width, local.Height);

        private static Rectangle RowLocal(int row)
            => new(BodyLocal.X + 1, BodyLocal.Y + ListHeaderHeight + 1 + row * RowHeight, BodyLocal.Width - 2, RowHeight);

        private static Rectangle ButtonLocal(int row, int button)
        {
            var rowRect = RowLocal(row);
            int y = rowRect.Y + (RowHeight - ButtonHeight) / 2;
            return button == UnpackButton
                ? new Rectangle(UnpackX, y, UnpackWidth, ButtonHeight)
                : new Rectangle(CombineX + button * (CombineWidth + ButtonGap), y, CombineWidth, ButtonHeight);
        }

        // ─────────────────────────────── Inventory state ───────────────────────────────

        private void AttachState()
        {
            var state = MuGame.Network?.GetCharacterState();
            if (ReferenceEquals(state, _state)) return;

            if (_state != null)
            {
                _state.InventoryChanged -= MarkDirty;
                _state.MoneyChanged -= MarkDirty;
            }

            _state = state;
            if (_state != null)
            {
                _state.InventoryChanged += MarkDirty;
                _state.MoneyChanged += MarkDirty;
            }
        }

        private void MarkDirty() => _dirty = true;

        private void Refresh()
        {
            _dirty = false;
            Array.Clear(_singles);
            Array.Clear(_packs);
            Array.Clear(_firstPackPieces);
            Array.Fill(_firstPackSlot, (byte)0xFF);
            _freeSlots = 0;
            _zen = _state?.InventoryZen ?? 0;
            if (_state == null) return;

            int extensions = Math.Clamp((int)_state.InventoryExpansionState, 0, MaxExtensions);
            var occupied = new bool[MainInventorySlots + extensions * ExtensionSlots];

            foreach (var entry in _state.GetInventoryItems())
            {
                int index = entry.Key - FirstInventorySlot;
                byte[] data = entry.Value;
                if (index < 0 || index >= occupied.Length || data == null || data.Length == 0) continue;

                MarkOccupied(occupied, index, ItemDatabase.GetItemDefinition(data));

                if (!ItemDatabase.TryGetItemGroupAndNumber(data, out byte group, out short number)) continue;
                for (int i = 0; i < Mixes.Length; i++)
                {
                    var mix = Mixes[i];
                    if (group == mix.Group && number == mix.Single)
                    {
                        _singles[i]++;
                        break;
                    }

                    if (group == PackGroup && number == mix.Pack)
                    {
                        _packs[i]++;
                        if (entry.Key < _firstPackSlot[i])
                        {
                            // Pack level 0/1/2 holds 10/20/30 jewels.
                            _firstPackSlot[i] = entry.Key;
                            _firstPackPieces[i] = (ItemDatabase.ParseItemDetails(data).Level + 1) * 10;
                        }
                        break;
                    }
                }
            }

            foreach (bool used in occupied)
            {
                if (!used) _freeSlots++;
            }
        }

        private static void MarkOccupied(bool[] occupied, int index, ItemDefinition? definition)
        {
            bool main = index < MainInventorySlots;
            int regionStart = main ? 0 : MainInventorySlots + (index - MainInventorySlots) / ExtensionSlots * ExtensionSlots;
            int regionRows = (main ? MainInventorySlots : ExtensionSlots) / GridColumns;
            int local = index - regionStart;
            int x = local % GridColumns, y = local / GridColumns;
            int width = Math.Max(1, definition?.Width ?? 1), height = Math.Max(1, definition?.Height ?? 1);

            for (int dy = 0; dy < height && y + dy < regionRows; dy++)
                for (int dx = 0; dx < width && x + dx < GridColumns; dx++)
                    occupied[regionStart + (y + dy) * GridColumns + x + dx] = true;
        }

        private static string FormatZen(uint value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>Whether a button can be used now, with a hint describing the action or why it is unavailable.</summary>
        private bool CanUse(int row, int button, out string hint)
        {
            var mix = Mixes[row];
            if (button < CombineButtons)
            {
                int count = (button + 1) * 10;
                uint fee = (uint)(button + 1) * CombineFeePerTen;
                if (_singles[row] < count)
                {
                    hint = $"Not enough {mix.Name}: you have {_singles[row]}, need {count}.";
                    return false;
                }

                if (_zen < fee)
                {
                    hint = $"Not enough Zen: combining {count} jewels costs {FormatZen(fee)} Zen.";
                    return false;
                }

                hint = $"Combine {count} {mix.Name} into a pack for {FormatZen(fee)} Zen.";
                return true;
            }

            if (_packs[row] == 0)
            {
                hint = $"You have no packed {mix.Name}.";
                return false;
            }

            int pieces = _firstPackSlot[row] != 0xFF ? _firstPackPieces[row] : 10;
            if (_zen < UnpackFee)
            {
                hint = $"Not enough Zen: unpacking costs {FormatZen(UnpackFee)} Zen.";
                return false;
            }

            // The server takes the fee before it checks the free space, so do not even try without enough room.
            if (_freeSlots < pieces)
            {
                hint = $"Unpacking {pieces} jewels needs {pieces} free inventory slots, you have {_freeSlots}.";
                return false;
            }

            hint = $"Unpack a pack of {pieces} {mix.Name} for {FormatZen(UnpackFee)} Zen.";
            return true;
        }

        private void OnButtonClicked(int row, int button)
        {
            if (!CanUse(row, button, out _)) return;
            var service = MuGame.Network?.GetCharacterService();
            if (service == null) return;

            var item = (LahapJewelMixRequest.ItemType)Mixes[row].Number;
            if (button < CombineButtons)
                _ = service.SendLahapJewelMixRequestAsync(LahapJewelMixRequest.MixType.Mix, item, (LahapJewelMixRequest.StackSize)button, 0);
            else
                _ = service.SendLahapJewelMixRequestAsync(LahapJewelMixRequest.MixType.Unmix, item, LahapJewelMixRequest.StackSize.Ten, _firstPackSlot[row]);

            SoundController.Instance.PlayBuffer("Sound/iButtonClick.wav");
        }

        // ─────────────────────────────── Update ───────────────────────────────

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (!Visible) return;

            // The NPC dialog was ended elsewhere (e.g. leaving the game): without it the server rejects the requests.
            if (MuGame.Network?.GetCharacterService()?.IsNpcDialogOpen != true)
            {
                Hide();
                return;
            }

            if (MuGame.Instance.Keyboard.IsKeyDown(Keys.Escape) && MuGame.Instance.PrevKeyboard.IsKeyUp(Keys.Escape))
            {
                Scene?.ConsumeKeyboardEscape();
                CloseWindow();
                return;
            }

            if (_dirty) Refresh();

            var mouse = MuGame.Instance.UiMouseState;
            var previous = MuGame.Instance.PrevUiMouseState;
            Point position = mouse.Position;
            bool leftDown = mouse.LeftButton == ButtonState.Pressed;
            bool leftPressed = leftDown && previous.LeftButton == ButtonState.Released;

            if (_isDragging)
            {
                if (leftDown) { X = position.X - _dragOffset.X; Y = position.Y - _dragOffset.Y; }
                else _isDragging = false;
                return;
            }

            UpdateHover(position);

            if (!leftPressed || !DisplayRectangle.Contains(position)) return;
            Scene?.SetMouseInputConsumed();

            if (_closeHovered)
            {
                CloseWindow();
                return;
            }

            if (_hoveredRow >= 0 && _hoveredButton >= 0)
            {
                OnButtonClicked(_hoveredRow, _hoveredButton);
                return;
            }

            if (Translate(new Rectangle(0, 0, WindowWidth, HeaderHeight)).Contains(position))
            {
                _isDragging = true;
                _dragOffset = new Point(position.X - X, position.Y - Y);
                Align = ControlAlign.None;
            }
        }

        private void UpdateHover(Point position)
        {
            _closeHovered = Translate(CloseLocal).Contains(position);
            _hoveredRow = -1;
            _hoveredButton = -1;

            for (int row = 0; row < Mixes.Length; row++)
            {
                if (!Translate(RowLocal(row)).Contains(position)) continue;
                _hoveredRow = row;
                for (int button = 0; button <= UnpackButton; button++)
                {
                    if (Translate(ButtonLocal(row, button)).Contains(position))
                    {
                        _hoveredButton = button;
                        break;
                    }
                }
                break;
            }
        }

        // ─────────────────────────────── Draw ───────────────────────────────

        public override void Draw(GameTime gameTime)
        {
            if (!Visible || Status != GameControlStatus.Ready) return;
            var sb = GraphicsManager.Instance.Sprite;
            var pixel = GraphicsManager.Instance.Pixel;
            if (sb == null || pixel == null) return;

            var scope = PlayerShopUi.BeginUiBatch();
            try
            {
                PlayerShopUi.DrawWindow(sb, DisplayRectangle, Alpha);
                PlayerShopUi.DrawHeader(sb, Translate(new Rectangle(0, 0, WindowWidth, HeaderHeight)), "LAHAP", "Jewel bundles", Alpha);
                PlayerShopUi.DrawCloseButton(sb, Translate(CloseLocal), _closeHovered, Alpha);

                var body = Translate(BodyLocal);
                PlayerShopUi.DrawPanel(sb, body, ModernHudTheme.BgDarkest, Alpha);
                DrawListHeader(sb, pixel, body);
                for (int row = 0; row < Mixes.Length; row++)
                    DrawRow(sb, pixel, row);

                DrawInfo(sb);
            }
            finally
            {
                scope?.Dispose();
            }
        }

        private void DrawListHeader(SpriteBatch sb, Texture2D pixel, Rectangle body)
        {
            var header = new Rectangle(body.X + 1, body.Y + 1, body.Width - 2, ListHeaderHeight - 1);
            sb.Draw(pixel, header, ModernHudTheme.BgMid * Alpha);
            int x = DisplayRectangle.X;
            PlayerShopUi.DrawText(sb, "Jewel", new Vector2(x + NameX, header.Y + 3), ModernHudTheme.TextGold, 11f, Alpha);
            PlayerShopUi.DrawTextCentered(sb, "Single", new Rectangle(x + SinglesX, header.Y, SinglesWidth, header.Height), ModernHudTheme.TextGold, 11f, Alpha);
            PlayerShopUi.DrawTextCentered(sb, "Packs", new Rectangle(x + PacksX, header.Y, PacksWidth, header.Height), ModernHudTheme.TextGold, 11f, Alpha);
            int combineWidth = CombineButtons * CombineWidth + (CombineButtons - 1) * ButtonGap;
            PlayerShopUi.DrawTextCentered(sb, "Combine", new Rectangle(x + CombineX, header.Y, combineWidth, header.Height), ModernHudTheme.TextGold, 11f, Alpha);
            PlayerShopUi.DrawTextCentered(sb, "Unpack", new Rectangle(x + UnpackX, header.Y, UnpackWidth, header.Height), ModernHudTheme.TextGold, 11f, Alpha);
        }

        private void DrawRow(SpriteBatch sb, Texture2D pixel, int row)
        {
            var mix = Mixes[row];
            var rect = Translate(RowLocal(row));
            if (_hoveredRow == row) sb.Draw(pixel, rect, Color.White * 0.06f * Alpha);
            else if (row % 2 == 1) sb.Draw(pixel, rect, Color.White * 0.025f * Alpha);

            int x = DisplayRectangle.X;
            float textY = rect.Y + (RowHeight - PlayerShopUi.Measure(mix.Name, 12f).Y) / 2f;
            bool hasAny = _singles[row] > 0 || _packs[row] > 0;
            PlayerShopUi.DrawText(sb, PlayerShopUi.Ellipsize(mix.Name, 12f, SinglesX - NameX - 6), new Vector2(x + NameX, textY),
                hasAny ? ModernHudTheme.TextWhite : ModernHudTheme.TextGray, 12f, Alpha);

            PlayerShopUi.DrawTextCentered(sb, _singles[row].ToString(CultureInfo.InvariantCulture), new Rectangle(x + SinglesX, rect.Y, SinglesWidth, RowHeight),
                _singles[row] >= 10 ? ModernHudTheme.TextWhite : ModernHudTheme.TextGray, 12f, Alpha);
            PlayerShopUi.DrawTextCentered(sb, _packs[row].ToString(CultureInfo.InvariantCulture), new Rectangle(x + PacksX, rect.Y, PacksWidth, RowHeight),
                _packs[row] > 0 ? ModernHudTheme.TextGold : ModernHudTheme.TextGray, 12f, Alpha);

            for (int button = 0; button <= UnpackButton; button++)
            {
                string label = button == UnpackButton ? "Unpack" : ((button + 1) * 10).ToString(CultureInfo.InvariantCulture);
                bool enabled = CanUse(row, button, out _);
                bool hovered = _hoveredRow == row && _hoveredButton == button;
                PlayerShopUi.DrawButton(sb, Translate(ButtonLocal(row, button)), label, hovered, enabled, ModernHudTheme.Accent, Alpha, 12f);
            }
        }

        private void DrawInfo(SpriteBatch sb)
        {
            int left = DisplayRectangle.X + Pad + 2;
            int right = DisplayRectangle.Right - Pad - 2;
            float y = DisplayRectangle.Y + InfoY;

            PlayerShopUi.DrawText(sb, $"Combine: {FormatZen(CombineFeePerTen)} Zen per 10 jewels (pack +0/+1/+2 = 10/20/30 jewels).",
                new Vector2(left, y), ModernHudTheme.TextGray, 11f, Alpha);
            y += LineHeight;
            PlayerShopUi.DrawText(sb, $"Unpack: {FormatZen(UnpackFee)} Zen; needs as many free inventory slots as jewels.",
                new Vector2(left, y), ModernHudTheme.TextGray, 11f, Alpha);
            y += LineHeight + 6;

            PlayerShopUi.DrawText(sb, $"Zen: {FormatZen(_zen)}", new Vector2(left, y), ModernHudTheme.TextGold, 12f, Alpha);
            string free = $"Free inventory slots: {_freeSlots}";
            PlayerShopUi.DrawText(sb, free, new Vector2(right - PlayerShopUi.Measure(free, 12f).X, y), ModernHudTheme.TextWhite, 12f, Alpha);
            y += LineHeight + 2;

            if (_hoveredRow >= 0 && _hoveredButton >= 0)
            {
                bool enabled = CanUse(_hoveredRow, _hoveredButton, out string hint);
                PlayerShopUi.DrawTextCentered(sb, hint, new Rectangle(left, (int)y, right - left, LineHeight),
                    enabled ? ModernHudTheme.Success : ModernHudTheme.Danger, 11f, Alpha);
            }
        }
    }
}
