using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Generator sekretów (hasła / token hex / GUID) — narzędzie inspirowane RDM.
    /// Bezstanowe: logika w <see cref="PasswordGen"/>, tu tylko UI. Niemodalne.
    /// </summary>
    public partial class PasswordGeneratorWindow
    {
        private bool _ready;
        private DispatcherTimer _copiedTimer;

        public PasswordGeneratorWindow()
        {
            InitializeComponent();
            _ready = true;
            SegPassword.IsChecked = true;   // wybiera typ „Hasło" → Type_Changed → Generate
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        // Typ z segmentów: 0 = hasło, 1 = token hex, 2 = GUID.
        private int CurrentType() => SegHex.IsChecked == true ? 1 : SegGuid.IsChecked == true ? 2 : 0;

        private void Type_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            int type = CurrentType();
            PasswordOptions.Visibility = type == 0 ? Visibility.Visible : Visibility.Collapsed;
            HexOptions.Visibility = type == 1 ? Visibility.Visible : Visibility.Collapsed;
            Generate();
        }

        private void Length_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LengthText != null) LengthText.Text = ((int)LengthSlider.Value).ToString();
            Generate();
        }

        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (BytesText != null) BytesText.Text = ((int)BytesSlider.Value).ToString();
            Generate();
        }

        // przeciążenie pod ValueChanged slidera bajtów (inny typ argumentu)
        private void Options_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Options_Changed(sender, (RoutedEventArgs)null);

        private void Generate_Click(object sender, RoutedEventArgs e) => Generate();

        private void Generate()
        {
            if (!_ready) return;
            switch (CurrentType())
            {
                case 1:
                    ResultBox.Text = PasswordGen.GenerateHexToken((int)BytesSlider.Value);
                    UpdateStrength((int)Math.Round(PasswordGen.EntropyBits((int)BytesSlider.Value * 2, 16)), true);
                    break;
                case 2:
                    ResultBox.Text = PasswordGen.GenerateGuid();
                    UpdateStrength(0, false);
                    break;
                default:
                    int len = (int)LengthSlider.Value;
                    bool u = OptUpper.IsChecked == true, lo = OptLower.IsChecked == true,
                         d = OptDigits.IsChecked == true, sy = OptSymbols.IsChecked == true,
                         noAmb = OptNoAmbiguous.IsChecked == true;
                    ResultBox.Text = PasswordGen.GeneratePassword(len, u, lo, d, sy, noAmb);
                    int pool = PasswordGen.BuildPool(u, lo, d, sy, noAmb).Length;
                    if (pool == 0) { StrengthText.Text = L("S.gen.pickclass"); SetBars(0, Res("Offline")); StrengthText.Foreground = Res("TextTer"); }
                    else UpdateStrength((int)Math.Round(PasswordGen.EntropyBits(len, pool)), true);
                    break;
            }
        }

        // Pasek siły: 0–4 segmenty w kolorze wg entropii (czerwony→bursztyn→niebieski→zielony) + etykieta.
        private void UpdateStrength(int bits, bool show)
        {
            if (!show) { StrengthText.Text = ""; SetBars(0, Res("Offline")); return; }
            int level = bits < 40 ? 1 : bits < 64 ? 2 : bits < 100 ? 3 : 4;
            Brush color = level == 1 ? Res("Danger") : level == 2 ? Res("Idle") : level == 3 ? Res("Accent") : Res("Online");
            SetBars(level, color);
            StrengthText.Text = string.Format(L("S.gen.strength"), bits);
            StrengthText.Foreground = color;
        }

        private void SetBars(int on, Brush onColor)
        {
            var dim = Res("Elevated");
            Sb1.Background = on >= 1 ? onColor : dim;
            Sb2.Background = on >= 2 ? onColor : dim;
            Sb3.Background = on >= 3 ? onColor : dim;
            Sb4.Background = on >= 4 ? onColor : dim;
        }

        private Brush Res(string key) => (Brush)(TryFindResource(key) ?? Brushes.Gray);

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ResultBox.Text)) return;
            try { Clipboard.SetText(ResultBox.Text); } catch { return; }

            CopiedText.Visibility = Visibility.Visible;
            if (_copiedTimer == null)
            {
                _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _copiedTimer.Tick += (s, a) => { _copiedTimer.Stop(); CopiedText.Visibility = Visibility.Collapsed; };
            }
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        private static string L(string key) => LocalizationManager.S(key);
    }
}
