﻿using EndlessClient.Content;
using EndlessClient.Dialogs.Services;
using EndlessClient.HUD.Chat;
using EOLib;
using EOLib.Graphics;
using Microsoft.Xna.Framework;
using XNAControls;

namespace EndlessClient.Dialogs
{
    public class TextInputDialog : BaseEODialog
    {
        private readonly IXNATextBox _inputBox;

        public string ResponseText => _inputBox.Text;

        public TextInputDialog(INativeGraphicsManager nativeGraphicsManager,
                               IChatTextBoxActions chatTextBoxActions,
                               IEODialogButtonService eoDialogButtonService,
                               IContentProvider contentProvider,
                               string prompt,
                               int maxInputChars = 12)
            : base(nativeGraphicsManager, isInGame: true)
        {
            BackgroundTexture = GraphicsManager.TextureFromResource(GFXTypes.PostLoginUI, 54);
            SetSize(BackgroundTexture.Width, BackgroundTexture.Height);

            var lblPrompt = new XNALabel(Constants.FontSize10)
            {
                AutoSize = false,
                DrawArea = new Rectangle(21, 19, 230, 49),
                ForeColor = ColorConstants.LightGrayDialogMessage,
                TextWidth = 225,
                Text = prompt
            };
            lblPrompt.Initialize();
            lblPrompt.SetParentControl(this);

            _inputBox = new XNATextBox(new Rectangle(37, 74, 192, 19), Constants.FontSize08, caretTexture: contentProvider.Textures[ContentProvider.Cursor])
            {
                MaxChars = maxInputChars,
                LeftPadding = 4,
                TextColor = ColorConstants.LightBeigeText,
            };
            _inputBox.SetParentControl(this);
                    
            var ok = new XNAButton(eoDialogButtonService.SmallButtonSheet,
                new Vector2(41, 103),
                eoDialogButtonService.GetSmallDialogButtonOutSource(SmallButton.Ok),
                eoDialogButtonService.GetSmallDialogButtonOverSource(SmallButton.Ok));
            ok.OnClick += (_, _) => Close(XNADialogResult.OK);
            ok.SetParentControl(this);

            var cancel = new XNAButton(eoDialogButtonService.SmallButtonSheet,
                new Vector2(134, 103),
                eoDialogButtonService.GetSmallDialogButtonOutSource(SmallButton.Cancel),
                eoDialogButtonService.GetSmallDialogButtonOverSource(SmallButton.Cancel));
            cancel.OnClick += (_, _) => Close(XNADialogResult.Cancel);
            cancel.SetParentControl(this);

            DialogClosed += (_, _) => chatTextBoxActions.FocusChatTextBox();

            CenterInGameView();
            DrawPosition += new Vector2(0, 17);
        }

        public override void Initialize()
        {
            _inputBox.Initialize();

            _inputBox.Selected = true;

            base.Initialize();
        }
    }
}
