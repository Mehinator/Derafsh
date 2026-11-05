using Microsoft.Maui.Controls.Shapes;
using QRCoder;

namespace Derafsh.Client
{
    // کد QR یه لینک (یه کانفیگ، یا لینک اشتراک) تا با دوربین یه گوشی دیگه اسکن بشه: v2rayNG، Hiddify، V2Box، Streisand
    // همه QR رو می‌خونن. کنارش «کپی لینک» و «فرستادن» هم هست.
    public class QrPage : ContentPage
    {
        public QrPage(string title, string text, string hint)
        {
            FlowDirection = FlowDirection.RightToLeft;
            BackgroundColor = Res<Color>("Bg");

            var status = new Label { Style = Res<Style>("MetaLabel"), HorizontalTextAlignment = TextAlignment.Center };
            var copy = new Button { Text = "کپی لینک", Style = Res<Style>("SecondaryButton") };
            copy.Clicked += async (_, _) =>
            {
                try { await Clipboard.Default.SetTextAsync(text); status.Text = "لینک کپی شد."; }
                catch { status.Text = "کپی نشد."; }
            };
            var share = new Button { Text = "فرستادن", Style = Res<Style>("SecondaryButton") };
            share.Clicked += async (_, _) =>
            {
                try { await Share.Default.RequestAsync(new ShareTextRequest { Text = text, Title = title }); }
                catch { status.Text = "فرستادن انجام نشد؛ «کپی لینک» را بزنید."; }
            };
            var close = new Button { Text = "بستن", Style = Res<Style>("SecondaryButton") };
            close.Clicked += async (_, _) => await Navigation.PopModalAsync();

            View qr = MakePng(text) is byte[] png
                ? new Border
                {
                    BackgroundColor = Res<Color>("QrPaper"),
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    HorizontalOptions = LayoutOptions.Center,
                    Content = new Image { Source = ImageSource.FromStream(() => new MemoryStream(png)), WidthRequest = 280, HeightRequest = 280, Aspect = Aspect.AspectFit },
                }
                : new Label { Text = "این لینک برای QR خیلی بلند است؛ «کپی لینک» یا «فرستادن» را بزنید.", TextColor = Res<Color>("TextSecondary"), HorizontalTextAlignment = TextAlignment.Center };
            SemanticProperties.SetDescription(qr, "کد QR " + title);

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(24, 32),
                    Spacing = 16,
                    MaximumWidthRequest = 440,
                    HorizontalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label { Text = title, Style = Res<Style>("TitleLabel"), HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 },
                        qr,
                        new Label { Text = hint, TextColor = Res<Color>("TextSecondary"), FontSize = 13, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap },
                        new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { copy, share, close } },
                        status,
                    },
                },
            };
        }

        // null: برای QR خیلی بلنده
        private static byte[]? MakePng(string text)
        {
            try
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                return new PngByteQRCode(data).GetGraphic(8);
            }
            catch { return null; }
        }

        // دکمه‌ی برگشت اندروید: فقط همین صفحه بسته بشه
        protected override bool OnBackButtonPressed()
        {
            _ = Navigation.PopModalAsync();
            return true;
        }

        private static T Res<T>(string key) => Application.Current!.Resources.TryGetValue(key, out var v) && v is T t ? t : default!;
    }
}
