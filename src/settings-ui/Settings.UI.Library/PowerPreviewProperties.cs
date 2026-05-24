// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Settings.UI.Library.Enumerations;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public class PowerPreviewProperties
    {
        public const string DefaultStlThumbnailColor = "#FFC924";
        public const int DefaultMonacoMaxFileSize = 50;
        public const int DefaultMonacoFontSize = 14;
        public const int DefaultSvgBackgroundColorMode = (int)SvgPreviewColorMode.Default;
        public const string DefaultSvgBackgroundSolidColor = "#FFFFFF";
        public const int DefaultSvgBackgroundCheckeredShade = (int)SvgPreviewCheckeredShade.Light;

        private bool enableSvgPreview = true;

        [JsonPropertyName("svg-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableSvgPreview
        {
            get => enableSvgPreview;
            set
            {
                if (value != enableSvgPreview)
                {
                    enableSvgPreview = value;
                }
            }
        }

        [JsonPropertyName("svg-previewer-background-color-mode")]
        public IntProperty SvgBackgroundColorMode { get; set; }

        [JsonPropertyName("svg-previewer-background-solid-color")]
        public StringProperty SvgBackgroundSolidColor { get; set; }

        [JsonPropertyName("svg-previewer-background-checkered-shade")]
        public IntProperty SvgBackgroundCheckeredShade { get; set; }

        private bool enableSvgThumbnail = true;

        [JsonPropertyName("svg-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableSvgThumbnail
        {
            get => enableSvgThumbnail;
            set
            {
                if (value != enableSvgThumbnail)
                {
                    enableSvgThumbnail = value;
                }
            }
        }

        private bool enableMdPreview = true;

        [JsonPropertyName("md-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableMdPreview
        {
            get => enableMdPreview;
            set
            {
                if (value != enableMdPreview)
                {
                    enableMdPreview = value;
                }
            }
        }

        private bool enableMonacoPreview = true;

        [JsonPropertyName("monaco-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableMonacoPreview
        {
            get => enableMonacoPreview;
            set
            {
                if (value != enableMonacoPreview)
                {
                    enableMonacoPreview = value;
                }
            }
        }

        private bool monacoPreviewWordWrap = true;

        [JsonPropertyName("monaco-previewer-toggle-setting-word-wrap")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableMonacoPreviewWordWrap
        {
            get => monacoPreviewWordWrap;
            set
            {
                if (value != monacoPreviewWordWrap)
                {
                    monacoPreviewWordWrap = value;
                }
            }
        }

        private bool monacoPreviewTryFormat;

        [JsonPropertyName("monaco-previewer-toggle-try-format")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool MonacoPreviewTryFormat
        {
            get => monacoPreviewTryFormat;
            set
            {
                if (value != monacoPreviewTryFormat)
                {
                    monacoPreviewTryFormat = value;
                }
            }
        }

        [JsonPropertyName("monaco-previewer-max-file-size")]
        public IntProperty MonacoPreviewMaxFileSize { get; set; }

        [JsonPropertyName("monaco-previewer-font-size")]
        public IntProperty MonacoPreviewFontSize { get; set; }

        private bool monacoPreviewStickyScroll = true;

        [JsonPropertyName("monaco-previewer-sticky-scroll")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool MonacoPreviewStickyScroll
        {
            get => monacoPreviewStickyScroll;
            set
            {
                if (value != monacoPreviewStickyScroll)
                {
                    monacoPreviewStickyScroll = value;
                }
            }
        }

        private bool monacoPreviewMinimap;

        [JsonPropertyName("monaco-previewer-minimap")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool MonacoPreviewMinimap
        {
            get => monacoPreviewMinimap;
            set
            {
                if (value != monacoPreviewMinimap)
                {
                    monacoPreviewMinimap = value;
                }
            }
        }

        private bool enablePdfPreview;

        [JsonPropertyName("pdf-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnablePdfPreview
        {
            get => enablePdfPreview;
            set
            {
                if (value != enablePdfPreview)
                {
                    enablePdfPreview = value;
                }
            }
        }

        private bool enablePdfThumbnail;

        [JsonPropertyName("pdf-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnablePdfThumbnail
        {
            get => enablePdfThumbnail;
            set
            {
                if (value != enablePdfThumbnail)
                {
                    enablePdfThumbnail = value;
                }
            }
        }

        private bool enableGcodePreview = true;

        [JsonPropertyName("gcode-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableGcodePreview
        {
            get => enableGcodePreview;
            set
            {
                if (value != enableGcodePreview)
                {
                    enableGcodePreview = value;
                }
            }
        }

        private bool enableBgcodePreview = true;

        [JsonPropertyName("bgcode-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableBgcodePreview
        {
            get => enableBgcodePreview;
            set
            {
                if (value != enableBgcodePreview)
                {
                    enableBgcodePreview = value;
                }
            }
        }

        private bool enableGcodeThumbnail = true;

        [JsonPropertyName("gcode-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableGcodeThumbnail
        {
            get => enableGcodeThumbnail;
            set
            {
                if (value != enableGcodeThumbnail)
                {
                    enableGcodeThumbnail = value;
                }
            }
        }

        private bool enableBgcodeThumbnail = true;

        [JsonPropertyName("bgcode-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableBgcodeThumbnail
        {
            get => enableBgcodeThumbnail;
            set
            {
                if (value != enableBgcodeThumbnail)
                {
                    enableBgcodeThumbnail = value;
                }
            }
        }

        private bool enableStlThumbnail = true;

        [JsonPropertyName("stl-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableStlThumbnail
        {
            get => enableStlThumbnail;
            set
            {
                if (value != enableStlThumbnail)
                {
                    enableStlThumbnail = value;
                }
            }
        }

        [JsonPropertyName("stl-thumbnail-color-setting")]
        public StringProperty StlThumbnailColor { get; set; }

        private bool enableQoiPreview = true;

        [JsonPropertyName("qoi-previewer-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableQoiPreview
        {
            get => enableQoiPreview;
            set
            {
                if (value != enableQoiPreview)
                {
                    enableQoiPreview = value;
                }
            }
        }

        private bool enableQoiThumbnail = true;

        [JsonPropertyName("qoi-thumbnail-toggle-setting")]
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableQoiThumbnail
        {
            get => enableQoiThumbnail;
            set
            {
                if (value != enableQoiThumbnail)
                {
                    enableQoiThumbnail = value;
                }
            }
        }

        public PowerPreviewProperties()
        {
            SvgBackgroundColorMode = new IntProperty(DefaultSvgBackgroundColorMode);
            SvgBackgroundSolidColor = new StringProperty(DefaultSvgBackgroundSolidColor);
            SvgBackgroundCheckeredShade = new IntProperty(DefaultSvgBackgroundCheckeredShade);
            StlThumbnailColor = new StringProperty(DefaultStlThumbnailColor);
            MonacoPreviewMaxFileSize = new IntProperty(DefaultMonacoMaxFileSize);
            MonacoPreviewFontSize = new IntProperty(DefaultMonacoFontSize);
        }

        public override string ToString()
        {
            return JsonSerializer.Serialize(this, SettingsSerializationContext.Default.PowerPreviewProperties);
        }
    }
}
