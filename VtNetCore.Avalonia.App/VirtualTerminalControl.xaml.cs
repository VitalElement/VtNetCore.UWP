using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;
using System.Text;
using System.Threading.Tasks;
using VtConnect;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace VtNetCore.Avalonia.App
{
    public class VirtualTerminalControl : TemplatedControl
    {
        public VirtualTerminalController Terminal { get; set; } = new VirtualTerminalController();
        public DataConsumer Consumer { get; set; }
        public int ViewTop { get; set; } = 0;
        public string WindowTitle { get; set; } = "Session";

        public bool ViewDebugging { get; set; }
        public bool DebugMouse { get; set; }
        public bool DebugSelect { get; set; }

        private char[] _rawText = new char[0];
        private int _rawTextLength = 0;
        private string _rawTextString = "";
        private bool _rawTextChanged = false;
        public DateTime TerminalIdleSince = DateTime.Now;

        public string RawText
        {
            get
            {
                if (_rawTextChanged)
                {
                    lock (_rawText)
                    {

                        _rawTextString = new string(_rawText, 0, _rawTextLength);
                        _rawTextChanged = false;
                    }
                }
                return _rawTextString;
            }
        }

        public VirtualTerminalControl()
        {
            this.TextInput += VirtualTerminalControl_TextInput;

            Consumer = new DataConsumer(Terminal);

            Terminal.SendData += OnSendData;
            Terminal.WindowTitleChanged += OnWindowTitleChanged;
            Terminal.OnLog += OnLog;
            Terminal.StoreRawText = true;

            var connected = ConnectTo("ssh://10.2.0.146", "osmc", "osmc");
        }

        private void VirtualTerminalControl_TextInput(object sender, TextInputEventArgs e)
        {
            var ch = e.Text;                        

            // Since I get the same key twice in TerminalKeyDown and in CoreWindow_CharacterReceived
            // I lookup whether KeyPressed should handle the key here or there.
            var code = Terminal.GetKeySequence(ch, false, false);
            if (code == null)
                e.Handled = Terminal.KeyPressed(ch, false, false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!Connected)
                return;

            var controlPressed = e.Modifiers.HasFlag(InputModifiers.Control);
            var shiftPressed = e.Modifiers.HasFlag(InputModifiers.Shift);

            if (controlPressed)
            {
                switch (e.Key)
                {
                    case Key.F10:
                        Consumer.SequenceDebugging = !Consumer.SequenceDebugging;
                        return;

                    case Key.F11:
                        ViewDebugging = !ViewDebugging;
                        InvalidateVisual();
                        return;

                    case Key.F12:
                        Terminal.Debugging = !Terminal.Debugging;
                        return;
                }
            }

            // Since I get the same key twice in TerminalKeyDown and in CoreWindow_CharacterReceived
            // I lookup whether KeyPressed should handle the key here or there.
            var code = Terminal.GetKeySequence(e.Key.ToString(), controlPressed, shiftPressed);
            if (code != null)
                e.Handled = Terminal.KeyPressed(e.Key.ToString(), controlPressed, shiftPressed);

            if (ViewTop != Terminal.ViewPort.TopRow)
            {
                Terminal.ViewPort.SetTopLine(ViewTop);
                InvalidateVisual();
            }
        }

        private void OnLog(object sender, TextEventArgs e)
        {
            //LogText += (e.Text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\n");
        }

        private void OnWindowTitleChanged(object sender, TextEventArgs e)
        {
            WindowTitle = e.Text;
        }

        private void OnSendData(object sender, SendDataEventArgs e)
        {
            if (!Connected)
                return;
            Task.Run(() =>
            {
                VtConnection.SendData(e.Data);
            });
        }

        public double CharacterWidth = -1;
        public double CharacterHeight = -1;
        public int Columns = -1;
        public int Rows = -1;

        public Connection VtConnection { get; set; }

        public bool Connected
        {
            get { return VtConnection != null && VtConnection.IsConnected; }
        }

        string InputBuffer { get; set; } = "";

        public void Disconnect()
        {
            if (!Connected)
                return;

            VtConnection.Disconnect();
            VtConnection.DataReceived -= OnDataReceived;
            VtConnection = null;
        }

        public bool ConnectTo(string uri, string username, string password)
        {
            if (Connected)
                return false;       // Already connected

            var credentials = new UsernamePasswordCredentials
            {
                Username = username,
                Password = password
            };

            var destination = new Uri(uri);

            VtConnection = Connection.CreateConnection(destination);
            VtConnection.SetTerminalWindowSize(Columns, Rows, 800, 600);

            VtConnection.DataReceived += OnDataReceived;

            var result = VtConnection.Connect(destination, credentials);

            return result;
        }

        private void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            lock (Terminal)
            {
                int oldTopRow = Terminal.ViewPort.TopRow;
                
                Consumer.Push(e.Data);

                if (Terminal.Changed)
                {
                    ProcessRawText();
                    Terminal.ClearChanges();

                    if (oldTopRow != Terminal.ViewPort.TopRow && oldTopRow >= ViewTop)
                        ViewTop = Terminal.ViewPort.TopRow;

                    this.InvalidateVisual();
                }

                TerminalIdleSince = DateTime.Now;
            }
        }

        private void ProcessRawText()
        {
            var incoming = Terminal.RawText;

            lock (_rawText)
            {
                if ((_rawTextLength + incoming.Length) > _rawText.Length)
                    Array.Resize(ref _rawText, _rawText.Length + 1000000);

                for (var i = 0; i < incoming.Length; i++)
                    _rawText[_rawTextLength++] = incoming[i];

                _rawTextChanged = true;
            }
        }

        public override void Render(DrawingContext context)
        {
            OnCanvasDraw(context);
        }

        private void OnCanvasDraw(DrawingContext drawingSession)
        {
            var format = new Typeface(FontFamily, FontSize, this.FontStyle, this.FontWeight);

            ProcessTextFormat();

            drawingSession.FillRectangle(GetBackgroundBrush(Terminal.CursorState.Attributes, false), new Rect(Bounds.Size));

            lock (Terminal)
            {
                int row = ViewTop;
                float verticalOffset = -row * (float)CharacterHeight;

                var lines = Terminal.ViewPort.GetLines(ViewTop, Rows);

                //var defaultTransform = drawingSession.Transform;
                foreach (var line in lines)
                {
                    if (line == null)
                    {
                        row++;
                        continue;
                    }

                    int column = 0;

                    using (drawingSession.PushPreTransform(Matrix.CreateScale(
                        (float)(line.DoubleWidth ? 2.0 : 1.0),
                        (float)(line.DoubleHeightBottom | line.DoubleHeightTop ? 2.0 : 1.0))))
                    {

                        var spanStart = 0;
                        while (column < line.Count)
                        {
                            bool selected = TextSelection == null ? false : TextSelection.Within(column, row);
                            var backgroundColor = GetBackgroundBrush(line[column].Attributes, selected);

                            if (column < (line.Count - 1) && GetBackgroundBrush(line[column + 1].Attributes, TextSelection == null ? false : TextSelection.Within(column + 1, row)) == backgroundColor)
                            {
                                column++;
                                continue;
                            }

                            var rect = new Rect(
                                spanStart * CharacterWidth,
                                ((row - (line.DoubleHeightBottom ? 1 : 0)) * CharacterHeight + verticalOffset) * (line.DoubleHeightBottom | line.DoubleHeightTop ? 0.5 : 1.0),
                                ((column - spanStart + 1) * CharacterWidth) + 0.9,
                                CharacterHeight + 0.9
                            );

                            drawingSession.FillRectangle(backgroundColor, rect);

                            column++;
                            spanStart = column;
                        }

                        row++;
                    }
                }                

                row = ViewTop;
                foreach (var line in lines)
                {
                    if (line == null)
                    {
                        row++;
                        continue;
                    }

                    int column = 0;

                    using (drawingSession.PushPreTransform(Matrix.CreateScale(
                        (float)(line.DoubleWidth ? 2.0 : 1.0),
                        (float)(line.DoubleHeightBottom | line.DoubleHeightTop ? 2.0 : 1.0))))
                    {

                        var spanStart = 0;
                        string toDisplay = string.Empty;
                        while (column < line.Count)
                        {
                            bool selected = TextSelection == null ? false : TextSelection.Within(column, row);
                            var foregroundColor = GetForegroundBrush(line[column].Attributes, selected);

                            toDisplay += line[column].Char.ToString() + line[column].CombiningCharacters;
                            if (
                                column < (line.Count - 1) &&
                                GetForegroundBrush(line[column + 1].Attributes, TextSelection == null ? false : TextSelection.Within(column + 1, row)) == foregroundColor &&
                                line[column + 1].Attributes.Underscore == line[column].Attributes.Underscore &&
                                line[column + 1].Attributes.Reverse == line[column].Attributes.Reverse &&
                                line[column + 1].Attributes.Bright == line[column].Attributes.Bright
                                )
                            {
                                column++;
                                continue;
                            }

                            var rect = new Rect(
                                spanStart * CharacterWidth,
                                ((row - (line.DoubleHeightBottom ? 1 : 0)) * CharacterHeight + verticalOffset) * (line.DoubleHeightBottom | line.DoubleHeightTop ? 0.5 : 1.0),
                                ((column - spanStart + 1) * CharacterWidth) + 0.9,
                                CharacterHeight + 0.9
                            );


                            var textLayout = new FormattedText
                            {
                                Text = toDisplay,
                                Typeface = format
                            };

                            drawingSession.DrawText(
                                foregroundColor,
                                rect.TopLeft,
                                textLayout
                            );

                            if (line[column].Attributes.Underscore)
                            {
                                drawingSession.DrawLine(new Pen(foregroundColor), rect.BottomLeft, rect.BottomRight);
                            }

                            column++;
                            spanStart = column;
                            toDisplay = "";
                        }

                        //foreach (var character in line)
                        //{
                        //    bool selected = TextSelection == null ? false : TextSelection.Within(column, row);

                        //    var rect = new Rect(
                        //        column * CharacterWidth,
                        //        ((row - (line.DoubleHeightBottom ? 1 : 0)) * CharacterHeight + verticalOffset) * (line.DoubleHeightBottom | line.DoubleHeightTop ? 0.5 : 1.0),
                        //        CharacterWidth + 0.9,
                        //        CharacterHeight + 0.9
                        //    );

                        //    var toDisplay = character.Char.ToString() + character.CombiningCharacters;

                        //    var textLayout = new CanvasTextLayout(drawingSession, toDisplay, format, 0.0f, 0.0f);
                        //    var foregroundColor = GetForegroundColor(character.Attributes, selected);

                        //    drawingSession.DrawTextLayout(
                        //        textLayout,
                        //        (float)rect.Left,
                        //        (float)rect.Top,
                        //        foregroundColor
                        //    );

                        //    if (character.Attributes.Underscore)
                        //    {
                        //        drawingSession.DrawLine(
                        //            new Vector2(
                        //                (float)rect.Left,
                        //                (float)rect.Bottom
                        //            ),
                        //            new Vector2(
                        //                (float)rect.Right,
                        //                (float)rect.Bottom
                        //            ),
                        //            foregroundColor
                        //        );
                        //    }

                        //    column++;
                        //}
                        row++;
                    }
                }                

                if (Terminal.CursorState.ShowCursor)
                {
                    var cursorY = Terminal.ViewPort.TopRow - ViewTop + Terminal.CursorState.CurrentRow;
                    var cursorRect = new Rect(
                        Terminal.CursorState.CurrentColumn * CharacterWidth,
                        cursorY * CharacterHeight,
                        CharacterWidth + 0.9,
                        CharacterHeight + 0.9
                    );

                    drawingSession.DrawRectangle(new Pen(GetForegroundBrush(Terminal.CursorState.Attributes, false)), cursorRect);
                }
            }

            //if (ViewDebugging)
            //    AnnotateView(drawingSession);
        }

        private static Color[] AttributeColors =
        {
            Color.FromArgb(255,0,0,0),        // Black
            Color.FromArgb(255,187,0,0),      // Red
            Color.FromArgb(255,0,187,0),      // Green
            Color.FromArgb(255,187,187,0),    // Yellow
            Color.FromArgb(255,0,0,187),      // Blue
            Color.FromArgb(255,187,0,187),    // Magenta
            Color.FromArgb(255,0,187,187),    // Cyan
            Color.FromArgb(255,187,187,187),  // White
            Color.FromArgb(255,85,85,85),     // Bright black
            Color.FromArgb(255,255,85,85),    // Bright red
            Color.FromArgb(255,85,255,85),    // Bright green
            Color.FromArgb(255,255,255,85),   // Bright yellow
            Color.FromArgb(255,85,85,255),    // Bright blue
            Color.FromArgb(255,255,85,255),   // Bright Magenta
            Color.FromArgb(255,85,255,255),   // Bright cyan
            Color.FromArgb(255,255,255,255),  // Bright white
        };

        private static SolidColorBrush[] AttributeBrushes =
        {
            new SolidColorBrush(AttributeColors[0]),
            new SolidColorBrush(AttributeColors[1]),
            new SolidColorBrush(AttributeColors[2]),
            new SolidColorBrush(AttributeColors[3]),
            new SolidColorBrush(AttributeColors[4]),
            new SolidColorBrush(AttributeColors[5]),
            new SolidColorBrush(AttributeColors[6]),
            new SolidColorBrush(AttributeColors[7]),
            new SolidColorBrush(AttributeColors[8]),
            new SolidColorBrush(AttributeColors[9]),
            new SolidColorBrush(AttributeColors[10]),
            new SolidColorBrush(AttributeColors[11]),
            new SolidColorBrush(AttributeColors[12]),
            new SolidColorBrush(AttributeColors[13]),
            new SolidColorBrush(AttributeColors[14]),
            new SolidColorBrush(AttributeColors[15]),
        };

        private Color GetBackgroundColor(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
            {
                if (attribute.Bright)
                    return AttributeColors[(int)attribute.ForegroundColor + 8];

                return AttributeColors[(int)attribute.ForegroundColor];
            }

            return AttributeColors[(int)attribute.BackgroundColor];
        }

        private Color GetForegroundColor(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
                return AttributeColors[(int)attribute.BackgroundColor];

            if (attribute.Bright)
                return AttributeColors[(int)attribute.ForegroundColor + 8];

            return AttributeColors[(int)attribute.ForegroundColor];
        }

        private IBrush GetBackgroundBrush(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
            {
                if (attribute.Bright)
                    return AttributeBrushes[(int)attribute.ForegroundColor + 8];

                return AttributeBrushes[(int)attribute.ForegroundColor];
            }

            return AttributeBrushes[(int)attribute.BackgroundColor];
        }

        private IBrush GetForegroundBrush(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
                return AttributeBrushes[(int)attribute.BackgroundColor];

            if (attribute.Bright)
                return AttributeBrushes[(int)attribute.ForegroundColor + 8];

            return AttributeBrushes[(int)attribute.ForegroundColor];
        }

        private void ProcessTextFormat()
        {            
            var textLayout = new FormattedText
            {
                Text = "Q",
                Typeface = new Typeface(FontFamily, FontSize, this.FontStyle, this.FontWeight)
        };

            var size = textLayout.Measure();
            if (CharacterWidth != size.Width || CharacterHeight != size.Height)
            {
                CharacterWidth = size.Width;
                CharacterHeight = size.Height;
            }

            int columns = Convert.ToInt32(Math.Floor(Bounds.Size.Width / CharacterWidth));
            int rows = Convert.ToInt32(Math.Floor(Bounds.Size.Height / CharacterHeight));
            if (Columns != columns || Rows != rows)
            {
                Columns = columns;
                Rows = rows;
                ResizeTerminal();

                if (VtConnection != null)
                    VtConnection.SetTerminalWindowSize(columns, rows, 800, 600);
            }
        }

        private void ResizeTerminal()
        {
            //System.Diagnostics.Debug.WriteLine("ResizeTerminal()");
            //System.Diagnostics.Debug.WriteLine("  Character size " + CharacterWidth.ToString() + "," + CharacterHeight.ToString());
            //System.Diagnostics.Debug.WriteLine("  Terminal size " + Columns.ToString() + "," + Rows.ToString());

            Terminal.ResizeView(Columns, Rows);
        }        

        private void TerminalKeyDown(object sender, KeyEventArgs e)
        {
            if (!Connected)
                return;

            var controlPressed = e.Modifiers.HasFlag(InputModifiers.Control);
            var shiftPressed = e.Modifiers.HasFlag(InputModifiers.Shift);

            if (controlPressed)
            {
                switch (e.Key)
                {
                    case Key.F10:
                        Consumer.SequenceDebugging = !Consumer.SequenceDebugging;
                        return;

                    case Key.F11:
                        ViewDebugging = !ViewDebugging;
                        InvalidateVisual();
                        return;

                    case Key.F12:
                        Terminal.Debugging = !Terminal.Debugging;
                        return;
                }
            }

            // Since I get the same key twice in TerminalKeyDown and in CoreWindow_CharacterReceived
            // I lookup whether KeyPressed should handle the key here or there.
            var code = Terminal.GetKeySequence(e.Key.ToString(), controlPressed, shiftPressed);
            if (code != null)
                e.Handled = Terminal.KeyPressed(e.Key.ToString(), controlPressed, shiftPressed);

            if (ViewTop != Terminal.ViewPort.TopRow)
            {
                Terminal.ViewPort.SetTopLine(ViewTop);
                InvalidateVisual();
            }

            //System.Diagnostics.Debug.WriteLine(e.Key.ToString() + ",S" + (shiftPressed ? "1" : "0") + ",C" + (controlPressed ? "1" : "0"));
        }

        //private void TerminalWheelChanged(object sender, PointerRoutedEventArgs e)
        //{
        //    var pointer = e.GetCurrentPoint(canvas);

        //    var controlPressed = (Window.Current.CoreWindow.GetKeyState(Key.Control).HasFlag(CoreVirtualKeyStates.Down));

        //    if (controlPressed)
        //    {
        //        double scale = 0.9 * (pointer.Properties.MouseWheelDelta / 120);

        //        var newFontSize = FontSize;
        //        if (scale < 0)
        //            newFontSize *= Math.Abs(scale);
        //        else
        //            newFontSize /= scale;

        //        if (newFontSize < 2)
        //            newFontSize = 2;
        //        if (newFontSize > 20)
        //            newFontSize = 20;

        //        if (newFontSize != FontSize)
        //        {
        //            FontSize = newFontSize;
        //            canvas.Invalidate();
        //        }
        //    }
        //    else
        //    {
        //        int oldViewTop = ViewTop;

        //        ViewTop -= pointer.Properties.MouseWheelDelta / 40;

        //        if (ViewTop < 0)
        //            ViewTop = 0;
        //        else if (ViewTop > Terminal.ViewPort.TopRow)
        //            ViewTop = Terminal.ViewPort.TopRow;

        //        if (oldViewTop != ViewTop)
        //            canvas.Invalidate();
        //    }
        //}

        TextPosition MouseOver { get; set; } = new TextPosition();

        TextRange TextSelection { get; set; }
        bool Selecting = false;

        private TextPosition ToPosition(Point point)
        {
            int overColumn = (int)Math.Floor(point.X / CharacterWidth);
            if (overColumn >= Columns)
                overColumn = Columns - 1;

            int overRow = (int)Math.Floor(point.Y / CharacterHeight);
            if (overRow >= Rows)
                overRow = Rows - 1;

            return new TextPosition { Column = overColumn, Row = overRow };
        }

        private void TerminalPointerMoved(object sender, PointerEventArgs e)
        {
            var pointer = e.GetPosition(this);
            var position = ToPosition(pointer);

            if (MouseOver != null && MouseOver == position)
                return;

            MouseOver = position;

            if (e.InputModifiers.HasFlag(InputModifiers.LeftMouseButton))
            {
                TextRange newSelection;
                var textPosition = position.OffsetBy(0, ViewTop);

                if (MousePressedAt != null && MousePressedAt != textPosition)
                {
                    if (MousePressedAt <= textPosition)
                    {
                        newSelection = new TextRange
                        {
                            Start = MousePressedAt,
                            End = textPosition.OffsetBy(-1, 0)
                        };
                    }
                    else
                    {
                        newSelection = new TextRange
                        {
                            Start = textPosition,
                            End = MousePressedAt
                        };
                    }

                    Selecting = true;

                    if (TextSelection != newSelection)
                    {
                        TextSelection = newSelection;

                        if (DebugSelect)
                            System.Diagnostics.Debug.WriteLine("Selection: " + TextSelection.ToString());

                        InvalidateVisual();
                    }
                }
            }

            if (DebugMouse)
                System.Diagnostics.Debug.WriteLine("Pointer Moved " + position.ToString());
        }

        private void TerminalPointerExited(object sender, PointerEventArgs e)
        {
            MouseOver = null;

            if (DebugMouse)
                System.Diagnostics.Debug.WriteLine("TerminalPointerExited()");

            InvalidateVisual();
        }

        public TextPosition MousePressedAt { get; set; }

        private void TerminalPointerPressed(object sender, PointerEventArgs e)
        {
            var pointer = e.GetPosition(this);
            var position = ToPosition(pointer);

            var textPosition = position.OffsetBy(0, ViewTop);
            if (e.InputModifiers.HasFlag(InputModifiers.LeftMouseButton))
                MousePressedAt = textPosition;
            else if (e.InputModifiers.HasFlag(InputModifiers.RightMouseButton))
                PasteClipboard();

            if (Connected && position.Column >= 0 && position.Row >= 0 && position.Column < Columns && position.Row < Rows)
            {
                throw new NotImplementedException();
                //var controlPressed = (Window.Current.CoreWindow.GetKeyState(Key.Control).HasFlag(CoreVirtualKeyStates.Down));
                //var shiftPressed = (Window.Current.CoreWindow.GetKeyState(Key.Shift).HasFlag(CoreVirtualKeyStates.Down));

                //var button =
                //    pointer.Properties.IsLeftButtonPressed ? 0 :
                //        pointer.Properties.IsRightButtonPressed ? 1 :
                //            2;  // Middle button

                //Terminal.MousePress(position.Column, position.Row, button, controlPressed, shiftPressed);
            }
        }

        private void TerminalPointerReleased(object sender, PointerEventArgs e)
        {
            var pointer = e.GetPosition(this);
            var position = ToPosition(pointer);
            var textPosition = position.OffsetBy(0, ViewTop);

            if (!e.InputModifiers.HasFlag(InputModifiers.LeftMouseButton))
            {
                if (Selecting)
                {
                    MousePressedAt = null;
                    Selecting = false;

                    if (DebugSelect)
                        System.Diagnostics.Debug.WriteLine("Captured : " + Terminal.GetText(TextSelection.Start.Column, TextSelection.Start.Row, TextSelection.End.Column, TextSelection.End.Row));

                    var captured = Terminal.GetText(TextSelection.Start.Column, TextSelection.Start.Row, TextSelection.End.Column, TextSelection.End.Row);

                    throw new NotImplementedException();
                    //Clipboard.SetContent(dataPackage);
                }
                else
                {
                    TextSelection = null;
                    InvalidateVisual();
                }
            }

            if (Connected && position.Column >= 0 && position.Row >= 0 && position.Column < Columns && position.Row < Rows)
            {
                //var controlPressed = (Window.Current.CoreWindow.GetKeyState(Key.Control).HasFlag(CoreVirtualKeyStates.Down));
                //var shiftPressed = (Window.Current.CoreWindow.GetKeyState(Key.Shift).HasFlag(CoreVirtualKeyStates.Down));

                //Terminal.MouseRelease(position.Column, position.Row, controlPressed, shiftPressed);
                throw new NotImplementedException();
            }
        }

        private void PasteText(string text)
        {
            if (VtConnection == null)
                return;

            Task.Run(() =>
            {
                var buffer = Encoding.UTF8.GetBytes(text);
                Task.Run(() =>
                {
                    VtConnection.SendData(buffer);
                });
            });
        }

        private void PasteClipboard()
        {
            //var package = Clipboard.GetContent();

            //Task.Run(async () =>
            //{
            //    string text = await package.GetTextAsync();
            //    if (!string.IsNullOrEmpty(text))
            //        PasteText(text);
            //});
        }
    }
}
