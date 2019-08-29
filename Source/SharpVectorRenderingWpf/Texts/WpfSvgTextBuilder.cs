﻿//-------------------------------------------------------------------------------
// Arabic Forms assignment to text characters based on codes from Apache Batik
//-------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;

using System.Windows;
using System.Windows.Media;

using SharpVectors.Dom.Svg;
using SharpVectors.Renderers.Wpf;

namespace SharpVectors.Renderers.Texts
{
    public sealed class WpfSvgTextBuilder : WpfTextBuilder
    {
        #region Private Fields

        private const string Whitespace     = "space"; // Some SVG fonts use 'space' for the whitespace (" ").
        private const string SmallCaps      = "small-caps";

        // Known typeface that support small-caps. Just leave it here! 
        private const string SmallCapsFont1 = "Palatino Linotype";
        private const string SmallCapsFont2 = "Perpetua Titling MT";
        private const string SmallCapsFont3 = "Copperplate Gothic";
        private const string SmallCapsFont4 = "Felix Titling";
        private const string SmallCapsFont5 = "Pescadero";           // Used by Microsoft for demo, but commercial

        private const string SmallCapsFont  = SmallCapsFont3;  // Selected font

        private double _textWidth;

        private string _fontVariant;
        private readonly SvgFontElement _font;

        private double _emScale;
        private SvgFontFaceElement _fontFaceElement;
        private SvgMissingGlyphElement _missingGlyph;

        // Maps and tables created from the font information...
        private SvgKerningTable _kerningTable;
        private SvgLatinGlyphMap _latinGlyphMaps;
        private SvgArabicGlyphMap _arabicGlyphMaps;

        // Fallbacks for missing character and formats...
        private WpfTextBuilder _missingFallBack;
        private WpfTextBuilder _smallCapFallBack;

        // A simple character iteractor to enable ordered glyph rendering...
        private AttributedTextIterator _textIterator;

        #endregion

        #region Constructors and Destructor

        public WpfSvgTextBuilder(CultureInfo culture, SvgFontElement font, double fontSize)
            : base(culture, fontSize)
        {
            _font = font;

            this.Initialize();
        }

        public WpfSvgTextBuilder(SvgFontElement font, CultureInfo culture, string fontName, 
            double fontSize, Uri fontUri = null) : base(culture, fontName, fontSize, fontUri)
        {
            _font = font;

            this.Initialize();
        }

        #endregion

        #region Public Properties

        public string FontVariant
        {
            get {
                return _fontVariant;
            }
            set {
                _fontVariant = value;
            }
        }

        public override WpfFontFamilyType FontFamilyType
        {
            get {
                return WpfFontFamilyType.Svg;
            }
        }

        public override double Alphabetic
        {
            get {
                if (_fontFaceElement != null)
                {
                    float alphabetic = _fontFaceElement.Alphabetic;
                    if (alphabetic.Equals(0))
                    {
                        return 0;
                    }
                    double alphabeticOffset = this.FontSizeInPoints * (_emScale / _fontSize) * alphabetic;
                    return _dpiY / 72f * alphabeticOffset;
                }
                return 0;
            }
        }

        public override double Ascent
        {
            get {
                if (_fontFaceElement != null)
                {
                    float ascent = _fontFaceElement.Ascent;
                    double baselineOffset = this.FontSizeInPoints * (_emScale / _fontSize) * ascent;
                    return _dpiY / 72f * baselineOffset;
                }
                return 0;
            }
        }

        public override double Width
        {
            get {
                return _textWidth;                     
            }
        }

        public string XmlLanguage
        {
            get {
                var culture = this.Culture;
                if (culture != null)
                {
                    return culture.TwoLetterISOLanguageName;
                }

                return string.Empty;
            }
        }

        #endregion

        #region Public Methods

        public override IList<Rect> MeasureChars(SvgTextContentElement element, string text, bool canBeWhitespace = true)
        {
            if (canBeWhitespace && string.IsNullOrEmpty(text))
            {
                return new List<Rect>();
            }
            if (!canBeWhitespace && string.IsNullOrWhiteSpace(text))
            {
                return new List<Rect>();
            }

            var textBounds = new List<Rect>();
            var path = this.Build(element, text, textBounds, false);
            return textBounds;
        }

        public override Size MeasureText(SvgTextContentElement element, string text, bool canBeWhitespace = true)
        {
            if (canBeWhitespace && string.IsNullOrEmpty(text))
            {
                return Size.Empty;
            }
            else if (string.IsNullOrWhiteSpace(text))
            {
                return Size.Empty;
            }

            var result   = new List<Rect>();
            var path     = this.Build(element, text, result, true);
            var nonEmpty = result.Where(r => r != Rect.Empty);
            if (!nonEmpty.Any())
            {
                return Size.Empty;
            }
            return new Size(nonEmpty.Last().Right - nonEmpty.First().Left, this.Baseline);
        }

        public override PathGeometry Build(SvgTextContentElement element, string text, double x, double y)
        {
            var textPath = this.Build(element, text, null, false);
            if (textPath.Figures != null && textPath.Figures.Count > 0)
            {
                textPath.Transform = new TranslateTransform(x, y);
            }

            return textPath;
        }

        #endregion

        #region Private Methods

        private PathGeometry Build(SvgTextContentElement element, string text, IList<Rect> textBounds, bool measureSpaces)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new PathGeometry();
            }

            _textIterator.Initialize(text);

            var textPath = new PathGeometry();

            SvgGlyphElement prevGlyph = null;
            double xPos = 0;

            var baseline = this.Baseline + this.Alphabetic; //TODO: Better calculation here!
            var xmlLang  = this.XmlLanguage; // Current language based on the lang/xml:lang of the text tag

            bool isAltGlyph = string.Equals(element.LocalName, "altGlyph", StringComparison.OrdinalIgnoreCase);
            SvgAltGlyphElement altGlyph = isAltGlyph ? (SvgAltGlyphElement)element : null;

            int itemCount = _textIterator.Count;
            for (int i = 0; i < itemCount; i++)
            {
                string inputText  = _textIterator[i];
                string arabicForm = _textIterator.GetArabicForm(i);

                SvgGlyphElement glyph = null;
                if (isAltGlyph)
                {
                    var altGlyphDef = altGlyph.ReferencedElement as SvgAltGlyphDefElement;
                    if (altGlyphDef != null)
                    {
                        if (altGlyphDef.IsSimple)
                        {
                            var glyphRef = altGlyphDef.GlyphRef;
                            if (glyphRef != null)
                            {
                                glyph = glyphRef.ReferencedElement as SvgGlyphElement;
                            }
                        }
                    }
                }
                else
                {
                    if (!_arabicGlyphMaps.TryGet(inputText, arabicForm, out glyph))
                    {
                        if (!_latinGlyphMaps.TryGet(inputText, xmlLang, out glyph))
                        {
                            if (string.IsNullOrWhiteSpace(xmlLang))
                            {
                                glyph = _missingGlyph;
                            }
                            prevGlyph = null;
                        }
                    }
                }
                if (glyph == null)
                {
                    // Handle this as fall back...
                    if (_missingFallBack == null)
                    {
                        _missingFallBack = Create(string.Empty, this.Culture, this.FontSize);
                    }

                    var missingPath = _missingFallBack.Build(element, inputText, xPos, baseline);
                    //var missingTransform = new TransformGroup();
                    //missingTransform.Children.Add(new ScaleTransform(_emScale, -1 * _emScale));
                    //missingTransform.Children.Add(new TranslateTransform(xPos, ascent));
                    //missingPath.Transform = new TranslateTransform(xPos, ascent);

                    if (textBounds != null)
                    {
                        Rect bounds = missingPath.Bounds;
                        if (measureSpaces && bounds == Rect.Empty)
                        {
                            textBounds.Add(new Rect(xPos, 0, glyph.HorizAdvX * _emScale, baseline));
                        }
                        else
                        {
                            textBounds.Add(bounds);
                        }
                    }

                    if (missingPath.Figures != null && missingPath.Figures.Count > 0)
                    {
                        textPath.AddGeometry(missingPath);
                    }

//                    xPos += missingPath.Bounds.Width;
                    xPos += _missingFallBack.Width;
                    continue;
                }
                if (!this.IsVariantMatched())
                {
                    bool isSmallCaps = false; // For simulation of small-caps
                    WpfTextBuilder selectedFallBack = null;
                    if (string.Equals(SmallCaps, _fontVariant, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_smallCapFallBack == null)
                        {
                            WpfFontFamilyInfo familyInfo = new WpfFontFamilyInfo(new FontFamily(SmallCapsFont), FontWeights.SemiBold,
                                FontStyles.Normal, FontStretches.Normal);
                            _smallCapFallBack = Create(familyInfo, this.Culture, this.FontSize);
                        }

                        selectedFallBack = _smallCapFallBack;
                        inputText   = inputText.ToUpper();
                        isSmallCaps = true;
                    }
                    else
                    {
                        if (_missingFallBack == null)
                        {
                            _missingFallBack = Create(string.Empty, this.Culture, this.FontSize);
                        }

                        selectedFallBack = _missingFallBack;
                    }

                    var missingPath = selectedFallBack.Build(element, inputText, xPos, baseline);
                    //var missingTransform = new TransformGroup();
                    //missingTransform.Children.Add(new ScaleTransform(_emScale, -1 * _emScale));
                    //missingTransform.Children.Add(new TranslateTransform(xPos, ascent));
                    //missingPath.Transform = missingTransform;

                    //TODO: For now simulate a small-cap
                    if (isSmallCaps)
                    {
                        var missingTransform = new TransformGroup();
                        missingTransform.Children.Add(new ScaleTransform(1, 0.7));
                        missingTransform.Children.Add(new TranslateTransform(0, 6));
                        missingPath.Transform = missingTransform;
                    }

                    if (textBounds != null)
                    {
                        Rect bounds = missingPath.Bounds;
                        if (measureSpaces && bounds == Rect.Empty)
                        {
                            textBounds.Add(new Rect(xPos, 0, glyph.HorizAdvX * _emScale, baseline));
                        }
                        else
                        {
                            textBounds.Add(bounds);
                        }
                    }

                    if (missingPath.Figures != null && missingPath.Figures.Count > 0)
                    {
                        textPath.AddGeometry(missingPath);
                    }
                    prevGlyph = null;

//                    xPos += missingPath.Bounds.Width;
                    xPos += selectedFallBack.Width;
                    continue;
                }

                if (prevGlyph != null && !_kerningTable.IsEmpty)
                {
                    xPos -= _kerningTable.GetValue(prevGlyph, glyph, _emScale);
                }
                PathGeometry glyphPath = new PathGeometry();
                glyphPath.Figures = PathFigureCollection.Parse(glyph.D);

                var groupTransform = new TransformGroup();
                groupTransform.Children.Add(new ScaleTransform(_emScale, -1 * _emScale));
                groupTransform.Children.Add(new TranslateTransform(xPos, baseline));
                glyphPath.Transform = groupTransform;

                if (textBounds != null)
                {
                    Rect bounds = glyphPath.Bounds;
                    if (measureSpaces && bounds == Rect.Empty)
                    {
                        textBounds.Add(new Rect(xPos, 0, glyph.HorizAdvX * _emScale, baseline));
                    }
                    else
                    {
                        textBounds.Add(bounds);
                    }
                }

                if (glyphPath.Figures != null && glyphPath.Figures.Count > 0)
                {
                    textPath.AddGeometry(glyphPath);
                }

                xPos += glyph.HorizAdvX * _emScale;
                prevGlyph = glyph;
            }

            _textWidth = xPos;

            return textPath;
        }

        private bool Initialize()
        {
            if (_font == null)
            {
                return false;
            }

            if (_fontFaceElement == null)
            {
                _fontFaceElement = _font.FontFace;
            }

            if (_missingGlyph == null)
            {
                _missingGlyph = _font.MissingGlyph;
            }

            if (_fontFaceElement == null)
            {
                return false;
            }
            if (_emScale <= 0.0d)
            {
                _emScale = _fontSize / _fontFaceElement.UnitsPerEm;
            }

            _textIterator = new SvgAttributedTextIterator();

            IList<SvgGlyphElement> glyphList = null;

            if (_latinGlyphMaps == null || _arabicGlyphMaps == null)
            {
                var neutralGlyphs = new SvgGlyphMap();
                // Add the language neutral map...
                _latinGlyphMaps  = new SvgLatinGlyphMap(neutralGlyphs);
                _arabicGlyphMaps = new SvgArabicGlyphMap(neutralGlyphs);

                glyphList = _font.Glyphs;

                if (glyphList != null && glyphList.Count != 0)
                {
                    foreach(var glyph in glyphList)
                    {
                        string glyphName = GetGlyphName(glyph); // glyph.Unicode ?? glyph.GlyphName ?? glyph.Id;

                        _textIterator.AddAttribute(glyphName);

                        string arabicForm = glyph.ArabicForm;
                        if (!string.IsNullOrWhiteSpace(arabicForm))
                        {
                            _arabicGlyphMaps.Add(glyphName, glyph, arabicForm);
                        }
                        else
                        {
                            string glyphLang = glyph.Lang;
                            if (string.IsNullOrWhiteSpace(glyphLang))
                            {
                                neutralGlyphs.Add(glyphName, glyph);
                                if (string.Equals(glyphName, Whitespace, StringComparison.OrdinalIgnoreCase))
                                {
                                    neutralGlyphs.Add(" ", glyph);
                                }
                            }
                            else
                            {
                                _latinGlyphMaps.Add(glyphName, glyph, glyphLang);
                            }
                        }
                    }
                }
            }

            if (_kerningTable == null)
            {
                _kerningTable = new SvgKerningTable(glyphList, _font);
            }

            return (_fontFaceElement != null && _missingGlyph != null && _latinGlyphMaps != null && _kerningTable != null);
        }

        private bool IsVariantMatched()
        {
            if (string.IsNullOrWhiteSpace(_fontVariant))
            {
                return true;
            }
            var faceVariant = _font.FontFace.FontVariant;

            bool isSmallCaps = string.Equals(faceVariant, SmallCaps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_fontVariant, SmallCaps, StringComparison.OrdinalIgnoreCase);
            if (!isSmallCaps)
            {
                return true;
            }
            return string.Equals(faceVariant, _fontVariant, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetGlyphName(SvgGlyphElement glyph)
        {
            if (!string.IsNullOrWhiteSpace(glyph.Unicode))
            {
                return glyph.Unicode;
            }
            if (!string.IsNullOrWhiteSpace(glyph.GlyphName))
            {
                return glyph.GlyphName;
            }
            if (!string.IsNullOrWhiteSpace(glyph.Id))
            {
                return glyph.Id;
            }
            return string.Empty;
        }

        #endregion

        #region SvgLatinGlyphMap Class

        private sealed class SvgGlyphMap : Dictionary<string, SvgGlyphElement>
        {
            private string _xmlLang;

            public SvgGlyphMap()
                : this(string.Empty)
            {
            }

            public SvgGlyphMap(string xmlLang)
                : base(StringComparer.Ordinal)
            {
                _xmlLang = xmlLang;
            }

            public string XmlLang
            {
                get {
                    return _xmlLang;
                }
            }
        }

        private sealed class SvgLatinGlyphMap : List<SvgGlyphMap>
        {
            private SvgGlyphMap _neutralMap;

            public SvgLatinGlyphMap(SvgGlyphMap neutralMap)
            {
                _neutralMap = neutralMap;
            }

            public void Add(string glyphName, SvgGlyphElement glyph, string xmlLang)
            {
                if (string.IsNullOrWhiteSpace(xmlLang))
                {
                    _neutralMap.Add(glyphName, glyph);
                    return;
                }

                var glyphMap = this.GetMap(xmlLang);
                if (glyphMap != null)
                {
                    glyphMap.Add(glyphName, glyph);
                }
            }

            public bool TryGet(string glyphName, string xmlLang, out SvgGlyphElement glyph)
            {
                bool isFound = false;

                glyph = null;
                if (_neutralMap.ContainsKey(glyphName))
                {
                    isFound = true;
                    glyph = _neutralMap[glyphName];
                }
                else
                {
                    var glyphMap = this.GetMap(xmlLang);
                    if (glyphMap != null && glyphMap.TryGetValue(glyphName, out glyph))
                    {
                        isFound = true;
                    }
                }

                return isFound;
            }

            private SvgGlyphMap GetMap(string xmlLang)
            {
                if (string.IsNullOrWhiteSpace(xmlLang))
                {
                    xmlLang = string.Empty;
                }
                else
                {
                    int index = xmlLang.IndexOf('-');
                    if (index > 0)
                    {
                        xmlLang = xmlLang.Substring(0, index);
                    }
                }

                if (_neutralMap == null)
                {
                    _neutralMap = new SvgGlyphMap();
                    this.Add(_neutralMap); // Add the language neutral map...
                }

                SvgGlyphMap glyphMap = null;
                foreach (SvgGlyphMap map in this)
                {
                    if (string.Equals(xmlLang, map.XmlLang))
                    {
                        glyphMap = map;
                        break;
                    }
                }
                if (glyphMap == null)
                {
                    glyphMap = new SvgGlyphMap(xmlLang);
                    this.Add(glyphMap);
                }
                return glyphMap;
            }
        }

        #endregion

        #region SvgArabicGlyphMap Class

        private sealed class SvgArabicGlyphMap : List<SvgGlyphMap>
        {
            private SvgGlyphMap _neutralMap;

            public SvgArabicGlyphMap(SvgGlyphMap neutralMap)
            {
                _neutralMap = neutralMap;
            }

            public void Add(string glyphName, SvgGlyphElement glyph, string arabicForm)
            {
                arabicForm = ArabicForms.GetForm(arabicForm);
                if (string.IsNullOrWhiteSpace(arabicForm))
                {
                    _neutralMap.Add(glyphName, glyph);
                    return;
                }

                var glyphMap = this.GetMap(arabicForm);
                if (glyphMap != null)
                {
                    glyphMap.Add(glyphName, glyph);
                }
            }

            public bool TryGet(string glyphName, string arabicForm, out SvgGlyphElement glyph)
            {
                bool isFound = false;

                glyph = null;
                if (_neutralMap.ContainsKey(glyphName))
                {
                    isFound = true;
                    glyph = _neutralMap[glyphName];
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(arabicForm) && ArabicForms.IsArabicForm(arabicForm))
                    {
                        var glyphMap = this.GetMap(arabicForm);
                        if (glyphMap != null)
                        {
                            if (glyphMap.ContainsKey(glyphName))
                            {
                                isFound = true;
                                glyph = glyphMap[glyphName];
                            }
                            return isFound;
                        }
                    }

                    foreach (var glyphMap in this)
                    {
                        if (glyphMap.ContainsKey(glyphName))
                        {
                            isFound = true;
                            glyph = glyphMap[glyphName];
                            break;
                        }
                    }
                }
                return isFound;
            }

            private SvgGlyphMap GetMap(string arabicForm)
            {
                if (_neutralMap == null)
                {
                    _neutralMap = new SvgGlyphMap();
                    this.Add(_neutralMap); // Add the language neutral map...
                }

                SvgGlyphMap glyphArabic = null;
                foreach (SvgGlyphMap map in this)
                {
                    if (string.Equals(arabicForm, map.XmlLang))
                    {
                        glyphArabic = map;
                        break;
                    }
                }
                if (glyphArabic == null)
                {
                    glyphArabic = new SvgGlyphMap(arabicForm);
                    this.Add(glyphArabic);
                }
                return glyphArabic;
            }
        }

        private static class ArabicForms
        {
            public const string None     = "";
            public const string Isolated = "isolated";
            public const string Initial  = "initial";
            public const string Medial   = "medial";
            public const string Terminal = "terminal";

            public static string GetForm(string input)
            {
                if (string.IsNullOrWhiteSpace(input))
                {
                    return string.Empty;
                }

                if (Isolated.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Isolated;
                }
                if (Initial.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Initial;
                }
                if (Medial.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Medial;
                }
                if (Terminal.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Terminal;
                }

                return string.Empty;
            }

            public static bool IsArabicForm(string currentForm)
            {
                switch (currentForm)
                {
                    case ArabicForms.None:
                        return false;
                    case ArabicForms.Isolated:
                        return true;
                    case ArabicForms.Terminal:
                        return true;
                    case ArabicForms.Initial:
                        return true;
                    case ArabicForms.Medial:
                        return true;
                }

                return false;
            }

            public static string GetNextForm(string currentForm)
            {
                switch (currentForm)
                {
                    case ArabicForms.None:
                        return ArabicForms.Isolated;
                    case ArabicForms.Isolated:
                        return ArabicForms.Terminal;
                    case ArabicForms.Terminal:
                        return ArabicForms.Initial;
                    case ArabicForms.Initial:
                        return ArabicForms.Medial;
                }

                return ArabicForms.None;
            }

            public static bool IsArabicText(string text)
            {
                char[] glyphs = text.ToCharArray();
                foreach (char glyph in glyphs)
                {
                    if (char.IsWhiteSpace(glyph))
                    {
                        continue;
                    }
                    if (glyph >= 0x600 && glyph <= 0x6ff)
                    {
                        continue;
                    }
                    if (glyph >= 0x750 && glyph <= 0x77f)
                    {
                        continue;
                    }
                    if (glyph >= 0xfb50 && glyph <= 0xfc3f)
                    {
                        continue;
                    }
                    if (glyph >= 0xfe70 && glyph <= 0xfefc)
                    {
                        continue;
                    }

                    return false;
                }
                return true;
            }
        }

        #endregion

        #region SvgKerningTable Class

        private sealed class KerningRange
        {
            private int _unicodeStart;
            private int _unicodeEnd;

            public KerningRange(string unicodeRange)
            {
                _unicodeStart = -1;
                _unicodeEnd   = -1;

                unicodeRange    = unicodeRange.Substring(2).Replace(" ", ""); // move pass the U+
                int hyphenIndex = unicodeRange.IndexOf('-');

                string startValue = string.Empty;
                string endValue   = string.Empty;
                if (hyphenIndex > 0)
                {
                    startValue = unicodeRange.Substring(0, hyphenIndex);
                    endValue   = unicodeRange.Substring(hyphenIndex + 1);
                }
                else if (unicodeRange.EndsWith("?", StringComparison.Ordinal))
                {
                    startValue = unicodeRange.Replace('?', '0');
                    endValue   = unicodeRange.Replace('?', 'F');
                }

                try
                {
                    _unicodeStart = Convert.ToInt32(startValue, 16);
                    _unicodeEnd   = Convert.ToInt32(endValue, 16);
                }
                catch (Exception)
                {
                    _unicodeStart = -1;
                    _unicodeEnd   = -1;
                }
            }

            public bool IsValid
            {
                get {
                    return (_unicodeStart != -1 && _unicodeEnd != -1);
                }
            }

            public bool Contains(int value)
            {
                return (this.IsValid && (value >= _unicodeStart) && (value <= _unicodeEnd));
            }
        }

        private sealed class KerningPair
        {
            private KerningRange _unicodes1;
            private KerningRange _unicodes2;
            private double _kerning;

            public KerningPair(KerningRange unicodes1, KerningRange unicodes2, double value)
            {
                _unicodes1 = unicodes1;
                _unicodes2 = unicodes2;
                _kerning = value;
            }

            public double Kerning
            {
                get {
                    return _kerning;
                }
            }

            public bool IsValid
            {
                get {
                    if ((_unicodes1 == null || !_unicodes1.IsValid) || (_unicodes2 == null || !_unicodes2.IsValid))
                    {
                        return false;
                    }
                    return !_kerning.Equals(0);
                }
            }

            public bool IsMatched(string unicode1, string unicode2)
            {
                if (unicode1.Length != 1 || unicode2.Length != 1)
                {
                    return false;
                }

                return this.IsMatched(unicode1[0], unicode2[0]);
            }

            public bool IsMatched(int unicode1, int unicode2)
            {
                return _unicodes1.Contains(unicode1) && _unicodes2.Contains(unicode2);
            }

            public double GetValue(double emScale)
            {
                return _kerning * emScale;
            }
        }

        private sealed class SvgKerningTable
        {
            private const string Separator      = "|";
            private const string UnicodeKeyword = "U+";

            private string _familyName;
            private IDictionary<string, double> _kerningMap;
            private IList<KerningPair> _kerningRanges;

            public SvgKerningTable(IList<SvgGlyphElement> glyphList, SvgFontElement font)
            {
                _familyName = font.FontFamily;
                _kerningMap = new Dictionary<string, double>(StringComparer.Ordinal);

                this.Initalize(glyphList, font);
            }

            public bool IsEmpty
            {
                get {
                    if ((_kerningMap != null && _kerningMap.Count != 0) ||
                        (_kerningRanges != null && _kerningRanges.Count != 0))
                    {
                        return false;
                    }
                    return true;
                }
            }

            public double GetValue(SvgGlyphElement prevGlyph, SvgGlyphElement nextGlyph, double emScale)
            {
                if (prevGlyph == null || nextGlyph == null)
                {
                    return 0;
                }

                if (_kerningRanges != null && _kerningRanges.Count != 0)
                {
                    foreach (var kerningRange in _kerningRanges)
                    {          
                        if (kerningRange.IsMatched(prevGlyph.Unicode, nextGlyph.Unicode))
                        {
                            return kerningRange.GetValue(emScale);
                        }
                    }
                }

                double kerning;

                string namePair = prevGlyph.GlyphName + Separator + nextGlyph.GlyphName;
                if (!string.IsNullOrWhiteSpace(namePair) && _kerningMap.TryGetValue(namePair, out kerning))
                {
                    return kerning * emScale;
                }

                string unicodePair = prevGlyph.Unicode + Separator + nextGlyph.Unicode;
                if (!string.IsNullOrWhiteSpace(unicodePair) && _kerningMap.TryGetValue(unicodePair, out kerning))
                {
                    return kerning * emScale;
                }

                return 0;
            }

            private void Initalize(IList<SvgGlyphElement> glyphList, SvgFontElement font)
            {
                var kernList = font.Kerning;
                if (kernList == null || kernList.Count == 0)
                {
                    return;
                }

                char[] charSeparators = { ',' };
                string[] glyphList1 = null;
                string[] glyphList2 = null;
                foreach (var kern in kernList)
                {
                    bool isFound = false;
                    bool isCommas = false;
                    string glyph1 = kern.Glyph1;
                    string glyph2 = kern.Glyph2;
                    if (!string.IsNullOrWhiteSpace(glyph1) && !string.IsNullOrWhiteSpace(glyph2))
                    {
                        if (glyph1.IndexOf(charSeparators[0]) > 0)
                        {
                            glyphList1 = glyph1.Split(charSeparators, StringSplitOptions.RemoveEmptyEntries);
                            isCommas = true;
                        }
                        if (glyph2.IndexOf(charSeparators[0]) > 0)
                        {
                            glyphList2 = glyph2.Split(charSeparators, StringSplitOptions.RemoveEmptyEntries);
                            isCommas = true;
                        }
                        if (isCommas)
                        {
                            if (glyphList1 != null && glyphList2 != null && glyphList1.Length == glyphList2.Length)
                            {
                                for (int i = 0; i < glyphList1.Length; i++)
                                {
                                    _kerningMap.Add(glyphList1[i] + Separator + glyphList2[i], kern.Kerning);
                                }
                            }
                        }
                        else
                        {
                            _kerningMap.Add(glyph1 + Separator + glyph2, kern.Kerning);
                        }
                        isFound = true;
                    }
                    string unicode1 = kern.Unicode1;
                    string unicode2 = kern.Unicode2;
                    if (!string.IsNullOrWhiteSpace(unicode1) && !string.IsNullOrWhiteSpace(unicode2))
                    {
                        bool isRanges = unicode1.StartsWith(UnicodeKeyword, StringComparison.OrdinalIgnoreCase) 
                            && unicode2.StartsWith(UnicodeKeyword, StringComparison.OrdinalIgnoreCase);
                        if (isRanges)
                        {
                            if (_kerningRanges == null)
                            {
                                _kerningRanges = new List<KerningPair>();
                            }

                            KerningRange range1 = new KerningRange(unicode1);
                            KerningRange range2 = new KerningRange(unicode2);

                            if (range1.IsValid && range2.IsValid)
                            {
                                KerningPair rangePair = new KerningPair(range1, range2, kern.Kerning);

                                _kerningRanges.Add(rangePair);
                                isFound = true;
                            }
                        }
                        else
                        {
                            if (unicode1.IndexOf(charSeparators[0]) > 0)
                            {
                                glyphList1 = unicode1.Split(charSeparators, StringSplitOptions.RemoveEmptyEntries);
                                isCommas = true;
                            }
                            if (unicode2.IndexOf(charSeparators[0]) > 0)
                            {
                                glyphList2 = unicode2.Split(charSeparators, StringSplitOptions.RemoveEmptyEntries);
                                isCommas = true;
                            }
                            if (isCommas)
                            {
                                if (glyphList1 != null && glyphList2 != null && glyphList1.Length == glyphList2.Length)
                                {
                                    for (int i = 0; i < glyphList1.Length; i++)
                                    {
                                        _kerningMap.Add(glyphList1[i] + Separator + glyphList2[i], kern.Kerning);
                                    }
                                }
                            }
                            else
                            {
                                _kerningMap.Add(unicode1 + Separator + unicode2, kern.Kerning);
                            }
                            isFound = true;
                        }
                    }

                    if (!isFound)
                    {
                        if (glyphList == null)
                        {
                            glyphList = font.Glyphs;
                        }

                        if (!string.IsNullOrWhiteSpace(unicode1) && !string.IsNullOrWhiteSpace(glyph2))
                        {
                            foreach (var glyph in glyphList)
                            {
                                if (string.Equals(glyph.GlyphName, glyph2))
                                {
                                    _kerningMap.Add(unicode1 + Separator + glyph.Unicode, kern.Kerning);
                                    break;
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(unicode2) && !string.IsNullOrWhiteSpace(glyph1))
                        {
                            foreach (var glyph in glyphList)
                            {
                                if (string.Equals(glyph.GlyphName, glyph1))
                                {
                                    _kerningMap.Add(glyph.Unicode + Separator + unicode2, kern.Kerning);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region AttributedTextIterator Class

        private abstract class AttributedTextIterator
        {
            protected IList<string> _attributes;

            protected AttributedTextIterator()
            {
                _attributes = new List<string>();
            }

            public abstract int Count { get; }
            public abstract string this[int index] { get; }

            public virtual void AddAttribute(string attribute)
            {
                if (string.IsNullOrWhiteSpace(attribute))
                {
                    return;
                }
                _attributes.Add(attribute);
            }

            public abstract void Initialize(string inputText);

            public abstract string GetArabicForm(int index);

            public int IndexOfAttribute(string attribute)
            {
                if (_attributes == null || _attributes.Count == 0 || string.IsNullOrWhiteSpace(attribute))
                {
                    return -1;
                }
                int indexAt = -1;
                for (int i = 0; i < _attributes.Count; i++)
                {
                    if (string.Equals(_attributes[i], attribute))
                    {
                        indexAt = i;
                        break;
                    }
                }
                return indexAt;
            }
        }

        private sealed class SvgAttributedTextIterator : AttributedTextIterator
        {
            private bool _isCharMode;
            private string _inputText;
            private IList<string> _multiCharList;
            private IList<string> _textList;
            private IList<string> _arabicForms;

            public SvgAttributedTextIterator()
            {
                _isCharMode = false;
                _inputText = string.Empty;
            }

            public override string this[int index]
            {
                get {
                    if (_isCharMode)
                    {
                        if (_inputText != null && _inputText.Length != 0)
                        {
                            if (index >= 0 && index < _inputText.Length)
                            {
                                return _inputText.Substring(index, 1);
                            }
                        }
                    }
                    if (_textList != null && _textList.Count != 0)
                    {
                        if (index >= 0 && index < _textList.Count)
                        {
                            return _textList[index];
                        }
                    }
                    return null;
                }
            }

            public override string GetArabicForm(int index)
            {
                if (_isCharMode)
                {
                    if (_arabicForms != null && _arabicForms.Count != 0)
                    {
                        if (index >= 0 && index < _arabicForms.Count)
                        {
                            return _arabicForms[index];
                        }
                    }
                }

                return string.Empty;
            }

            public override int Count
            {
                get {
                    if (_isCharMode)
                    {
                        return _inputText.Length;
                    }
                    if (_textList != null)
                    {
                        return _textList.Count;
                    }
                    return 0;
                }
            }

            public override void AddAttribute(string attribute)
            {
                if (!string.IsNullOrWhiteSpace(attribute) && attribute.Length > 1)
                {
                    if (string.Equals(attribute, Whitespace, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (_multiCharList == null)
                    {
                        _multiCharList = new List<string>();
                    }
                    _multiCharList.Add(attribute);
                }
                base.AddAttribute(attribute);
            }

            public override void Initialize(string inputText)
            {
                bool isArabic = ArabicForms.IsArabicText(inputText);

                if (isArabic)
                {
                    char[] charArray = inputText.ToCharArray();
                    Array.Reverse(charArray);
                    _inputText = new string(charArray);
                }
                else
                {
                    _inputText = string.Copy(inputText);
                }

                if (_multiCharList == null || _multiCharList.Count == 0)
                {
                    _isCharMode = true;

                    if (isArabic)
                    {
                        this.BuildArabicForms();
                    }

                    return;
                }

                _isCharMode = false;

                _textList = new List<string>();

                int iStart = 0;
                int iEnd = _inputText.Length;

                while (iStart < iEnd)
                {
                    string remainText = _inputText.Substring(iStart);
                    string multiText = null;
                    for (int i = 0; i < _multiCharList.Count; i++)
                    {
                        if (remainText.StartsWith(_multiCharList[i], StringComparison.Ordinal))
                        {
                            multiText = _multiCharList[i];
                            break;
                        }
                    }
                    if (multiText != null)
                    {
                        var startText = remainText.Substring(0, 1);
                        int startCharIndex = this.IndexOfAttribute(startText);
                        int multiCharIndex = this.IndexOfAttribute(multiText);
                        if (startCharIndex < 0 || multiCharIndex < startCharIndex)
                        {
                            _textList.Add(multiText);
                            iStart += multiText.Length;
                        }
                        else
                        {
                            _textList.Add(startText);
                            iStart++;
                        }
                        continue;
                    }

                    _textList.Add(_inputText.Substring(iStart, 1));

                    iStart++;
                }
            }

            private void BuildArabicForms()
            {
                if (!_isCharMode)
                {
                    return; // Currently, only the character mode is supported
                }
                int itemCount = _inputText.Length;
                _arabicForms = new List<string>(itemCount);

                // first assign none to all arabic letters
                for (int i = 0; i < itemCount; i++)
                {
                    _arabicForms.Add(ArabicForms.None);
                }

                int startIndex = 0;
                int endIndex = itemCount;
                int currentIndex = startIndex;
                int previousIndex = startIndex - 1;

                string currentForm = ArabicForms.None;

                char currentChar = _inputText[startIndex];
                while (currentIndex < endIndex)
                {
                    char prevChar = currentChar;
                    currentChar = _inputText[currentIndex];
                    while (ArabicCharTransparent(currentChar) && (currentIndex < endIndex))
                    {
                        currentIndex++;
                        currentChar = _inputText[currentIndex];
                    }
                    if (currentIndex >= endIndex)
                    {
                        break;
                    }

                    var prevForm = currentForm;
                    currentForm = ArabicForms.None;
                    if (previousIndex >= startIndex)
                    {
                        // if not at the start
                        // if prev char right AND current char left
                        if (ArabicCharShapesRight(prevChar) && ArabicCharShapesLeft(currentChar))
                        {
                            // Increment the form of the previous char
                            prevForm = ArabicForms.GetNextForm(prevForm);
                            SetArabicAttribute(prevForm, previousIndex, previousIndex + 1);

                            // and set the form of the current char to INITIAL
                            currentForm = ArabicForms.Initial;
                        }
                        else if (ArabicCharShaped(currentChar))
                        {
                            // set the form of the current char to ISOLATE
                            currentForm = ArabicForms.Isolated;
                        }

                        // if this is the first arabic char and its shaped, set to ISOLATE
                    }
                    else if (ArabicCharShaped(currentChar))
                    {
                        // set the form of the current char to ISOLATE
                        currentForm = ArabicForms.Isolated;
                    }
                    if (currentForm != ArabicForms.None)
                    {
                        SetArabicAttribute(currentForm, currentIndex, currentIndex + 1);
                    }
                    previousIndex = currentIndex;
                    currentIndex++;
                }
            }

            private void SetArabicAttribute(string arabicForm, int start, int end)
            {
                for (int i = start; i < end; i++)
                {
                    _arabicForms[i] = arabicForm;
                }
            }

            /// <summary>
            /// Returns true if the char is transparent.
            /// </summary>
            /// <param name="c"> The character to test. </param>
            /// <returns> True if the character is transparent, false otherwise. </returns>
            public static bool ArabicCharTransparent(char c)
            {
                int charVal = c;
                if ((charVal < 0x064B) || (charVal > 0x06ED))
                    return false;

                if ((charVal <= 0x0655) ||
                    (charVal == 0x0670) ||
                    (charVal >= 0x06D6 && charVal <= 0x06E4) ||
                    (charVal >= 0x06E7 && charVal <= 0x06E8) ||
                    (charVal >= 0x06EA))
                {
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Returns true if the character shapes to the right. Note that duel
            /// shaping characters also shape to the right and so will return true.
            /// </summary>
            /// <param name="c"> The character to test. </param>
            /// <returns> True if the character shapes to the right, false otherwise. </returns>
            private static bool ArabicCharShapesRight(char c)
            {
                int charVal = c;
                if ((charVal >= 0x0622 && charVal <= 0x0625)
                 || (charVal == 0x0627)
                 || (charVal == 0x0629)
                 || (charVal >= 0x062F && charVal <= 0x0632)
                 || (charVal == 0x0648)
                 || (charVal >= 0x0671 && charVal <= 0x0673)
                 || (charVal >= 0x0675 && charVal <= 0x0677)
                 || (charVal >= 0x0688 && charVal <= 0x0699)
                 || (charVal == 0x06C0)
                 || (charVal >= 0x06C2 && charVal <= 0x06CB)
                 || (charVal == 0x06CD)
                 || (charVal == 0x06CF)
                 || (charVal >= 0x06D2 && charVal <= 0x06D3)
                 // check for duel shaping too
                 || ArabicCharShapesDuel(c))
                {
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Returns true if character has duel shaping.
            /// </summary>
            /// <param name="c"> The character to test. </param>
            /// <returns> True if the character is duel shaping, false otherwise. </returns>
            private static bool ArabicCharShapesDuel(char c)
            {
                int charVal = c;

                if ((charVal == 0x0626)
                 || (charVal == 0x0628)
                 || (charVal >= 0x062A && charVal <= 0x062E)
                 || (charVal >= 0x0633 && charVal <= 0x063A)
                 || (charVal >= 0x0641 && charVal <= 0x0647)
                 || (charVal >= 0x0649 && charVal <= 0x064A)
                 || (charVal >= 0x0678 && charVal <= 0x0687)
                 || (charVal >= 0x069A && charVal <= 0x06BF)
                 || (charVal == 0x6C1)
                 || (charVal == 0x6CC)
                 || (charVal == 0x6CE)
                 || (charVal >= 0x06D0 && charVal <= 0x06D1)
                 || (charVal >= 0x06FA && charVal <= 0x06FC))
                {
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Returns true if character shapes to the left. Note that duel
            /// shaping characters also shape to the left and so will return true.
            /// </summary>
            /// <param name="c"> The character to test. </param>
            /// <returns> True if the character shapes to the left, false otherwise. </returns>
            private static bool ArabicCharShapesLeft(char c)
            {
                return ArabicCharShapesDuel(c);
            }

            /// <summary>
            /// Returns true if character is shaped.
            /// </summary>
            /// <param name="c"> The character to test. </param>
            /// <returns> True if the character is shaped, false otherwise. </returns>
            private static bool ArabicCharShaped(char c)
            {
                return ArabicCharShapesRight(c);
            }
        }

        #endregion
    }
}
