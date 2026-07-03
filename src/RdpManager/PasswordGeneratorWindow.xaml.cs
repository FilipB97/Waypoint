using System;
using System.Windows;
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
            TypeCombo.SelectedIndex = 0;
            _ready = true;
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
            int type = TypeCombo.SelectedIndex;
            PasswordOptions.Visibility = type == 0 ? Visibility.Visible : Visibility.Collapsed;
            HexOptions.Visibility = type == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (BytesText != null) BytesText.Text = ((int)BytesSlider.Value).ToString();
            Generate();
        }

        // przeciążenie pod ValueChanged slidera bajtów (inny typ argumentu)
        private void Options_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Options_Changed(sender, (RoutedEventArgs)null);

        private void Generate_Click(object sender, RoutedEventArgs e) => Generate();

        private void Generate()
        {
            if (!_ready) return;
            switch (TypeCombo.SelectedIndex)
            {
                case 1:
                    ResultBox.Text = PasswordGen.GenerateHexToken((int)BytesSlider.Value);
                    StrengthText.Text = string.Format(L("S.gen.strength"),
                        (int)Math.Round(PasswordGen.EntropyBits((int)BytesSlider.Value * 2, 16)));
                    break;
                case 2:
                    ResultBox.Text = PasswordGen.GenerateGuid();
                    StrengthText.Text = "";
                    break;
                default:
                    int len = (int)LengthSlider.Value;
                    bool u = OptUpper.IsChecked == true, lo = OptLower.IsChecked == true,
                         d = OptDigits.IsChecked == true, sy = OptSymbols.IsChecked == true,
                         noAmb = OptNoAmbiguous.IsChecked == true;
                    ResultBox.Text = PasswordGen.GeneratePassword(len, u, lo, d, sy, noAmb);
                    int pool = PasswordGen.BuildPool(u, lo, d, sy, noAmb).Length;
                    StrengthText.Text = pool == 0
                        ? L("S.gen.pickclass")
                        : string.Format(L("S.gen.strength"), (int)Math.Round(PasswordGen.EntropyBits(len, pool)));
                    break;
            }
        }

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
