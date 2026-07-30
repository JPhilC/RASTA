using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RASTA.App.Controls
{
    /// <summary>
    /// A TextBox-derived control for entering frequency values in Hz.
    /// Displays values in the format 000.000.000.000 (range 1 – 999,999,999,999 Hz).
    /// Supports digit-by-digit overwrite entry, caret navigation, Up/Down stepping,
    /// mouse-wheel adjustment, Backspace, Delete, Home and End keys.
    /// </summary>
    public class FrequencyEntryBox : TextBox
    {
        // ---- Layout constants -----------------------------------------------
        //  Display string: "000.000.000.000"  (15 chars, 12 digit positions)
        //  Separator positions in the display string: 3, 7, 11
        //
        //  Display index:  0  1  2  .  4  5  6  .  8  9  10  .  12  13  14
        //  Digit index:    0  1  2     3  4  5     6  7   8       9  10  11

        private static readonly int[] DisplayPosToDigitIndex =
        [
            0, 1, 2, -1,   // 0-3  (3 = '.')
            3, 4, 5, -1,   // 4-7  (7 = '.')
            6, 7, 8, -1,   // 8-11 (11 = '.')
            9, 10, 11      // 12-14
        ];

        private static readonly int[] DigitIndexToDisplayPos =
        [
            0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14
        ];

        private const long MinHz = 1L;
        private const long MaxHz = 999_999_999_999L;

        private bool _internalUpdate;

        // ---------------------------------------------------------
        // Dependency Property
        // ---------------------------------------------------------

        public static readonly DependencyProperty FrequencyHzProperty =
            DependencyProperty.Register(
                nameof(FrequencyHz), typeof(long), typeof(FrequencyEntryBox),
                new FrameworkPropertyMetadata(
                    MinHz,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnFrequencyChanged));

        public long FrequencyHz
        {
            get => (long)GetValue(FrequencyHzProperty);
            set => SetValue(FrequencyHzProperty, value);
        }

        private static void OnFrequencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (FrequencyEntryBox)d;
            if (!ctrl._internalUpdate)
                ctrl.UpdateTextFromValue();
        }

        // ---------------------------------------------------------
        // Constructor
        // ---------------------------------------------------------

        public FrequencyEntryBox()
        {
            FontFamily = new FontFamily("Consolas, Courier New");
            MaxLength = 15; // "000.000.000.000"

            Loaded += (_, _) =>
            {
                UpdateTextFromValue();
                MoveCaretToDigit(0);
            };

            PreviewTextInput += OnPreviewTextInput;
            PreviewKeyDown += OnPreviewKeyDown;
            MouseWheel += OnMouseWheel;
            PreviewMouseDown += (_, _) => Dispatcher.InvokeAsync(SnapCaretOffSeparator);

            DataObject.AddPastingHandler(this, OnPaste);
        }

        // ---------------------------------------------------------
        // Formatting / Parsing
        // ---------------------------------------------------------

        private static string Format(long hz)
        {
            string d = Math.Clamp(hz, MinHz, MaxHz).ToString("000000000000");
            return $"{d[..3]}.{d[3..6]}.{d[6..9]}.{d[9..12]}";
        }

        private static long ParseText(string text)
        {
            string cleaned = text.Replace(".", "").Trim();
            return long.TryParse(cleaned, out long hz)
                ? Math.Clamp(hz, MinHz, MaxHz)
                : MinHz;
        }

        private void UpdateTextFromValue()
        {
            _internalUpdate = true;
            int caret = CaretIndex;
            Text = Format(FrequencyHz);
            CaretIndex = Math.Clamp(caret, 0, Text.Length);
            _internalUpdate = false;
        }

        // ---------------------------------------------------------
        // Caret helpers
        // ---------------------------------------------------------

        /// <summary>Returns the digit index (0-11) at the current caret position, or -1 if on a separator.</summary>
        private int CurrentDigitIndex()
        {
            int pos = CaretIndex;
            if (pos < 0 || pos >= DisplayPosToDigitIndex.Length) return -1;
            return DisplayPosToDigitIndex[pos];
        }

        private void MoveCaretToDigit(int digitIndex)
        {
            digitIndex = Math.Clamp(digitIndex, 0, 11);
            CaretIndex = DigitIndexToDisplayPos[digitIndex];
        }

        private void SnapCaretOffSeparator()
        {
            int pos = CaretIndex;
            if (pos >= 0 && pos < DisplayPosToDigitIndex.Length && DisplayPosToDigitIndex[pos] == -1)
                CaretIndex = pos + 1;
        }

        // ---------------------------------------------------------
        // Digit overwrite
        // ---------------------------------------------------------

        private void OverwriteDigit(int digitIndex, int newDigit)
        {
            string digits = Text.Replace(".", "");
            if (digits.Length != 12)
                digits = Math.Clamp(FrequencyHz, MinHz, MaxHz).ToString("000000000000");

            char[] arr = digits.ToCharArray();
            arr[digitIndex] = (char)('0' + newDigit);

            long value = Math.Clamp(long.Parse(new string(arr)), MinHz, MaxHz);

            _internalUpdate = true;
            FrequencyHz = value;
            Text = Format(value);
            _internalUpdate = false;
        }

        // ---------------------------------------------------------
        // Input filtering – overwrite mode
        // ---------------------------------------------------------

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = true; // suppress default insertion in all cases

            if (e.Text.Length == 0 || !char.IsDigit(e.Text[0]))
                return;

            int digitIndex = CurrentDigitIndex();
            if (digitIndex == -1)
            {
                // Caret landed on a separator – nudge it past, then retry
                SnapCaretOffSeparator();
                digitIndex = CurrentDigitIndex();
                if (digitIndex == -1) return;
            }

            OverwriteDigit(digitIndex, e.Text[0] - '0');

            // Advance to next digit position
            if (digitIndex < 11)
                MoveCaretToDigit(digitIndex + 1);
        }

        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            e.CancelCommand(); // always handle ourselves

            if (!e.DataObject.GetDataPresent(DataFormats.Text))
                return;

            string pasted = ((string)e.DataObject.GetData(DataFormats.Text)).Replace(".", "").Trim();
            if (!long.TryParse(pasted, out long value))
                return;

            _internalUpdate = true;
            FrequencyHz = Math.Clamp(value, MinHz, MaxHz);
            Text = Format(FrequencyHz);
            _internalUpdate = false;

            MoveCaretToDigit(0);
        }

        // ---------------------------------------------------------
        // Keyboard navigation
        // ---------------------------------------------------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Allow clipboard shortcuts and Tab to pass through normally
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && (e.Key == Key.C || e.Key == Key.A || e.Key == Key.X))
                return;
            if (e.Key == Key.Tab)
                return;

            int digitIndex = CurrentDigitIndex();
            if (digitIndex == -1) digitIndex = 0;

            switch (e.Key)
            {
                case Key.Up:
                    StepDigit(digitIndex, +1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    StepDigit(digitIndex, -1);
                    e.Handled = true;
                    break;

                case Key.Left:
                    if (digitIndex > 0) MoveCaretToDigit(digitIndex - 1);
                    e.Handled = true;
                    break;

                case Key.Right:
                    if (digitIndex < 11) MoveCaretToDigit(digitIndex + 1);
                    e.Handled = true;
                    break;

                case Key.Back:
                    int prev = Math.Max(digitIndex - 1, 0);
                    OverwriteDigit(prev, 0);
                    MoveCaretToDigit(prev);
                    e.Handled = true;
                    break;

                case Key.Delete:
                    OverwriteDigit(digitIndex, 0);
                    e.Handled = true;
                    break;

                case Key.Home:
                    MoveCaretToDigit(0);
                    e.Handled = true;
                    break;

                case Key.End:
                    MoveCaretToDigit(11);
                    e.Handled = true;
                    break;

                default:
                    e.Handled = true; // block unrecognised keys
                    break;
            }
        }

        // ---------------------------------------------------------
        // Digit stepping (Up/Down arrow, mouse wheel)
        // ---------------------------------------------------------

        private void StepDigit(int digitIndex, int direction)
        {
            long step = (long)Math.Pow(10, 11 - digitIndex);
            long newValue = Math.Clamp(FrequencyHz + direction * step, MinHz, MaxHz);

            _internalUpdate = true;
            FrequencyHz = newValue;
            Text = Format(newValue);
            _internalUpdate = false;

            MoveCaretToDigit(digitIndex); // keep caret stable after update
        }

        // ---------------------------------------------------------
        // Mouse wheel
        // ---------------------------------------------------------

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            int digitIndex = CurrentDigitIndex();
            if (digitIndex < 0) return;

            StepDigit(digitIndex, e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        }

        // ---------------------------------------------------------
        // Suppress TextChanged side-effects while we own the update
        // ---------------------------------------------------------

        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            base.OnTextChanged(e);

            if (_internalUpdate) return;

            // External text change (e.g. binding reset) – re-parse and reformat
            long value = ParseText(Text);
            _internalUpdate = true;
            FrequencyHz = value;
            Text = Format(value);
            _internalUpdate = false;
        }
    }
}
