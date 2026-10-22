using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Quattromani.Core;

/// <summary>
/// Export e import SVG. L'export scrive sempre un file che altri programmi aprono senza perdite: gradienti e motivi diventano
/// <c>linearGradient</c>/<c>radialGradient</c>/<c>pattern</c>, il testo resta testo, i tracciati restano curve.
/// L'import legge il sottoinsieme che serve alle illustrazioni (forme, path, gruppi, testo, immagini incorporate, gradienti, use, CSS a classi);
/// quello che il modello non sa rappresentare (clip, maschere, filtri, tratteggi...) viene saltato e segnalato nelle note.
/// </summary>
public static class SvgFile
{
    public const string Extension = ".svg";

    static readonly XNamespace Svg = "http://www.w3.org/2000/svg", Xlink = "http://www.w3.org/1999/xlink", Inkscape = "http://www.inkscape.org/namespaces/inkscape",
        Sodipodi = "http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd";

    static string N(double v, int decimals = 3)
    {
        var r = Math.Round(v, decimals);
        return (r == 0 ? 0 : r).ToString("0.######", CultureInfo.InvariantCulture);
    }

    // ---- Export --------------------------------------------------------------------------------

    public static string Export(Document doc)
    {
        var page = doc.Pages[0];
        var defs = new XElement(Svg + "defs");
        var writer = new Writer(defs);
        var root = new XElement(Svg + "svg",
            new XAttribute(XNamespace.Xmlns + "xlink", Xlink),
            new XAttribute(XNamespace.Xmlns + "inkscape", Inkscape),
            new XAttribute(XNamespace.Xmlns + "sodipodi", Sodipodi),
            new XAttribute("width", N(page.Width) + "px"), new XAttribute("height", N(page.Height) + "px"),
            new XAttribute("viewBox", $"0 0 {N(page.Width)} {N(page.Height)}"));
        var layers = new List<XElement>();
        var i = 0;
        foreach (var layer in page.Layers)
        {
            var g = new XElement(Svg + "g", new XAttribute("id", $"layer{++i}"),
                new XAttribute(Inkscape + "groupmode", "layer"), new XAttribute(Inkscape + "label", layer.Name));
            if (!layer.Visible) g.SetAttributeValue("display", "none");
            if (layer.Locked) g.SetAttributeValue(Sodipodi + "insensitive", "true");
            foreach (var o in layer.Objects) g.Add(writer.Object(o));
            layers.Add(g);
        }
        if (defs.HasElements) root.Add(defs);
        root.Add(layers);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root.ToString();
    }

    sealed class Writer(XElement defs)
    {
        int _n;

        static string Matrix(Matrix3x2 m) =>
            $"matrix({N(m.M11, 6)} {N(m.M12, 6)} {N(m.M21, 6)} {N(m.M22, 6)} {N(m.M31, 4)} {N(m.M32, 4)})";

        static void Transform(XElement e, Matrix3x2 m)
        {
            if (!m.IsIdentity) e.SetAttributeValue("transform", Matrix(m));
        }

        public XElement Object(SceneObject o)
        {
            XElement e;
            switch (o)
            {
                case GroupObject g:
                    e = new XElement(Svg + "g");
                    Transform(e, g.Fit * g.Transform);
                    foreach (var c in g.Children) e.Add(Object(c));
                    break;
                case PathObject p:
                    e = new XElement(Svg + "path", new XAttribute("d", PathData.ToString(PathData.Transform(p.Segments, p.Fit), 3)));
                    Transform(e, p.Transform);
                    Style(e, o, new Box(o.X, o.Y, o.Width, o.Height));
                    break;
                case TextObject { PathGlyphs: { } glyphs } t:
                    e = new XElement(Svg + "path", new XAttribute("d", PathData.ToString(PathData.Transform(glyphs, t.Fit), 3)));
                    Transform(e, t.Transform);
                    Style(e, o, new Box(o.X, o.Y, o.Width, o.Height));
                    break;
                case TextObject t:
                    e = Text(t);
                    break;
                case EllipseObject:
                    e = new XElement(Svg + "ellipse", new XAttribute("cx", N(o.X + o.Width / 2)), new XAttribute("cy", N(o.Y + o.Height / 2)),
                        new XAttribute("rx", N(o.Width / 2)), new XAttribute("ry", N(o.Height / 2)));
                    Transform(e, o.Transform);
                    Style(e, o, new Box(o.X, o.Y, o.Width, o.Height));
                    break;
                case ImageObject im:
                    e = Image(im);
                    break;
                default:
                    e = new XElement(Svg + "rect", new XAttribute("x", N(o.X)), new XAttribute("y", N(o.Y)),
                        new XAttribute("width", N(o.Width)), new XAttribute("height", N(o.Height)));
                    Transform(e, o.Transform);
                    Style(e, o, new Box(o.X, o.Y, o.Width, o.Height));
                    break;
            }
            e.SetAttributeValue("id", o.Id);
            if (o.Name != null) e.SetAttributeValue("data-name", o.Name);
            if (o.Hidden) e.SetAttributeValue("display", "none");
            if (o.Locked) e.SetAttributeValue(Sodipodi + "insensitive", "true");
            return e;
        }

        XElement Text(TextObject t)
        {
            var e = new XElement(Svg + "text", new XAttribute(XNamespace.Xml + "space", "preserve"),
                new XAttribute("font-family", t.FontFamily.Contains(' ') ? $"'{t.FontFamily}'" : t.FontFamily),
                new XAttribute("font-size", N(t.FontSize)));
            if (t.Bold) e.SetAttributeValue("font-weight", "bold");
            if (t.Italic) e.SetAttributeValue("font-style", "italic");
            Transform(e, t.Fit * t.Transform);
            Style(e, t, t.Source);
            foreach (var line in t.Lines)
                e.Add(new XElement(Svg + "tspan", new XAttribute("x", N(t.Source.X + line.X)), new XAttribute("y", N(t.Source.Y + line.Y)), line.Text));
            return e;
        }

        // Sfondo (fill) sotto l'immagine, cornice (stroke) sopra: come nel disegno a schermo.
        XElement Image(ImageObject im)
        {
            var e = new XElement(Svg + "g");
            Transform(e, im.Transform);
            var box = new Box(im.X, im.Y, im.Width, im.Height);
            XElement Rect() => new(Svg + "rect", new XAttribute("x", N(im.X)), new XAttribute("y", N(im.Y)), new XAttribute("width", N(im.Width)), new XAttribute("height", N(im.Height)));
            if (im.Fill != null)
            {
                var bg = Rect();
                Paint(bg, "fill", im.Fill, box);
                e.Add(bg);
            }
            e.Add(new XElement(Svg + "image", new XAttribute("x", N(im.X)), new XAttribute("y", N(im.Y)), new XAttribute("width", N(im.Width)), new XAttribute("height", N(im.Height)),
                new XAttribute("preserveAspectRatio", "none"),
                new XAttribute(Xlink + "href", $"data:{Mime(im.Data)};base64,{Convert.ToBase64String(im.Data)}")));
            if (im.Stroke != null && im.StrokeWidth > 0)
            {
                var frame = Rect();
                frame.SetAttributeValue("fill", "none");
                Paint(frame, "stroke", im.Stroke, box);
                frame.SetAttributeValue("stroke-width", N(im.StrokeWidth));
                e.Add(frame);
            }
            return e;
        }

        static string Mime(byte[] d) =>
            d.Length > 3 && d[0] == 0x89 && d[1] == 0x50 ? "image/png" :
            d.Length > 2 && d[0] == 0xFF && d[1] == 0xD8 ? "image/jpeg" :
            d.Length > 3 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' ? "image/gif" :
            d.Length > 11 && d[0] == 'R' && d[1] == 'I' && d[8] == 'W' && d[9] == 'E' ? "image/webp" :
            d.Length > 1 && d[0] == 'B' && d[1] == 'M' ? "image/bmp" : "application/octet-stream";

        void Style(XElement e, SceneObject o, Box box)
        {
            if (o.Fill is null) e.SetAttributeValue("fill", "none");
            else Paint(e, "fill", o.Fill, box);
            if (o.Stroke != null && o.StrokeWidth > 0)
            {
                Paint(e, "stroke", o.Stroke, box);
                e.SetAttributeValue("stroke-width", N(o.StrokeWidth));
            }
        }

        // "#RRGGBBAA" diventa colore + opacità, che è quello che SVG capisce.
        static void Color(XElement e, string attr, string color)
        {
            e.SetAttributeValue(attr, color[..7]);
            if (color.Length == 9)
                e.SetAttributeValue(attr == "stop-color" ? "stop-opacity" : attr + "-opacity", N(Convert.ToInt32(color[7..], 16) / 255.0));
        }

        void Paint(XElement e, string attr, string spec, Box box)
        {
            if (Gradient.IsGradient(spec)) e.SetAttributeValue(attr, $"url(#{GradientDef(Gradient.Parse(spec, attr), box)})");
            else if (Pattern.IsPattern(spec)) e.SetAttributeValue(attr, $"url(#{PatternDef(Pattern.Parse(spec, attr), box)})");
            else Color(e, attr, spec);
        }

        string GradientDef(GradientSpec g, Box b)
        {
            var id = $"grad{++_n}";
            XElement def;
            if (!g.Radial)
            {
                var (x1, y1, x2, y2) = Gradient.LinearPoints(g, b);
                def = new XElement(Svg + "linearGradient", new XAttribute("x1", N(b.X + x1 * b.Width)), new XAttribute("y1", N(b.Y + y1 * b.Height)),
                    new XAttribute("x2", N(b.X + x2 * b.Width)), new XAttribute("y2", N(b.Y + y2 * b.Height)));
            }
            else
            {
                var (fx, fy) = Gradient.RadialRadii(g, b);
                double cx = b.X + g.CenterX * b.Width, cy = b.Y + g.CenterY * b.Height, rx = Math.Max(fx * b.Width, 1e-6), ry = fy * b.Height;
                def = new XElement(Svg + "radialGradient", new XAttribute("cx", N(cx)), new XAttribute("cy", N(cy)), new XAttribute("r", N(rx)));
                // L'ellisse è un cerchio di raggio rx stirato in verticale attorno al centro.
                if (Math.Abs(ry - rx) > 1e-6)
                    def.SetAttributeValue("gradientTransform", $"translate({N(cx)} {N(cy)}) scale(1 {N(ry / rx, 6)}) translate({N(-cx)} {N(-cy)})");
            }
            def.SetAttributeValue("id", id);
            def.SetAttributeValue("gradientUnits", "userSpaceOnUse");
            foreach (var s in g.Stops)
            {
                var stop = new XElement(Svg + "stop", new XAttribute("offset", N(s.Position, 4)));
                Color(stop, "stop-color", s.Color);
                def.Add(stop);
            }
            defs.Add(def);
            return id;
        }

        // Stessa unità di MakePatternShader: parte dall'angolo in alto a sinistra dell'oggetto e ruota dell'angolo indicato.
        string PatternDef(PatternSpec p, Box b)
        {
            var id = $"pat{++_n}";
            double s = p.Size, tile = p.Kind == "checker" ? 2 * s : s;
            var def = new XElement(Svg + "pattern", new XAttribute("id", id), new XAttribute("patternUnits", "userSpaceOnUse"),
                new XAttribute("width", N(tile)), new XAttribute("height", N(tile)),
                new XAttribute("patternTransform", $"translate({N(b.X)} {N(b.Y)}) rotate({N(p.Angle)})"));
            XElement Rect(double x, double y, double w, double h, string color)
            {
                var r = new XElement(Svg + "rect", new XAttribute("x", N(x)), new XAttribute("y", N(y)), new XAttribute("width", N(w)), new XAttribute("height", N(h)));
                Color(r, "fill", color);
                return r;
            }
            def.Add(Rect(0, 0, tile, tile, p.Bg));
            switch (p.Kind)
            {
                case "stripes": def.Add(Rect(0, 0, s / 2, tile, p.Fg)); break;
                case "dots":
                    var dot = new XElement(Svg + "circle", new XAttribute("cx", N(s / 2)), new XAttribute("cy", N(s / 2)), new XAttribute("r", N(s * 0.3)));
                    Color(dot, "fill", p.Fg);
                    def.Add(dot);
                    break;
                case "checker": def.Add(Rect(0, 0, s, s, p.Fg), Rect(s, s, s, s, p.Fg)); break;
                case "grid":
                    var t = Math.Max(s * 0.12, 0.5);
                    def.Add(Rect(0, 0, tile, t, p.Fg), Rect(0, 0, t, tile, p.Fg));
                    break;
            }
            defs.Add(def);
            return id;
        }
    }

    // ---- Import --------------------------------------------------------------------------------

    /// <summary>Riquadro che l'illustrazione occuperebbe importandola così com'è (nelle sue coordinate), per poterla centrare o scalare prima di importarla.</summary>
    public static Box Measure(Document doc, string svg) => Build(doc, svg, null).Group.Source;

    /// <summary>Costruisce il gruppo con tutto il contenuto dell'SVG (senza id: li assegna chi lo inserisce nel documento).</summary>
    internal static (GroupObject Group, List<string> Notes) Build(Document doc, string svg, string? name)
    {
        XDocument xml;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            xml = XDocument.Load(reader);
        }
        catch (XmlException ex) { throw new CommandException($"svg.import: the file is not valid XML/SVG ({ex.Message})."); }
        if (xml.Root is not { } root || root.Name.LocalName != "svg")
            throw new CommandException("svg.import: the root element must be <svg>.");

        var importer = new Importer(doc);
        var group = importer.Run(root, name);
        return (group, importer.Notes);
    }

    internal static void AssignIds(Document doc, SceneObject o)
    {
        o.Id = doc.NewId();
        if (o is GroupObject g) foreach (var c in g.Children) AssignIds(doc, c);
    }

    sealed record St(string Fill = "black", string Stroke = "none", double StrokeWidth = 1, double Opacity = 1, double FillOpacity = 1, double StrokeOpacity = 1,
        string FontFamily = "", double FontSize = 16, bool Bold = false, bool Italic = false, string Anchor = "start");

    sealed class Importer(Document doc)
    {
        static readonly Regex Number = new(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.Compiled);
        static readonly Regex LengthRx = new(@"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(px|pt|pc|mm|cm|in|em|ex|%)?$", RegexOptions.Compiled);
        static readonly Regex TransformRx = new(@"(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
        static readonly Regex UrlRx = new(@"^url\(\s*['""]?#([^'"")\s]+)['""]?\s*\)\s*(.*)$", RegexOptions.Compiled);
        static readonly Regex GenericId = new(@"^(?:o\d+|(?:path|rect|circle|ellipse|g|text|image|use|line|polygon|polyline|svg|layer|shape|group|tspan|defs|symbol|lineargradient|radialgradient)[\d_-]*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex CssRule = new(@"([^{}]+)\{([^{}]*)\}", RegexOptions.Compiled);
        static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        const int MaxDepth = 40;

        public readonly List<string> Notes = [];
        readonly HashSet<string> _noted = [];
        readonly Dictionary<string, XElement> _ids = [];
        readonly Dictionary<string, Dictionary<string, string>> _css = [];
        double _vw = 100, _vh = 100;
        int _depth;

        void Note(string s)
        {
            if (Notes.Count < 12 && _noted.Add(s)) Notes.Add(s);
        }

        public GroupObject Run(XElement root, string? name)
        {
            foreach (var e in root.DescendantsAndSelf())
            {
                if (e.Attribute("id") is { } id) _ids.TryAdd(id.Value, e);
                if (e.Name.LocalName == "style") ParseCss(e.Value);
            }

            // Misura e sistema di coordinate: viewBox + width/height (px; un'eventuale unità la converte).
            var vb = Nums(root.Attribute("viewBox")?.Value);
            var hasVb = vb.Length == 4 && vb[2] > 0 && vb[3] > 0;
            _vw = hasVb ? vb[2] : 100; _vh = hasVb ? vb[3] : 100;
            var w = Len(root.Attribute("width")?.Value, _vw); var h = Len(root.Attribute("height")?.Value, _vh);
            var m = Matrix3x2.Identity;
            if (hasVb)
            {
                double sx = (w ?? vb[2]) / vb[2], sy = (h ?? vb[3]) / vb[3];
                var s = Math.Min(sx, sy);
                m = Matrix3x2.CreateTranslation((float)-vb[0], (float)-vb[1]) * Matrix3x2.CreateScale((float)s);
            }
            else { _vw = w ?? 100; _vh = h ?? 100; }

            var kids = new List<SceneObject>();
            var st = Inherit(new St(), Props(root));
            foreach (var e in root.Elements()) Visit(e, st, kids);
            if (kids.Count == 0)
                throw new CommandException("svg.import: nothing to import (the SVG is empty or uses only unsupported elements such as clips, masks or filters).");

            var rootTransform = ParseTransform(root.Attribute("transform")?.Value) * m;
            foreach (var k in kids) k.Transform *= rootTransform;
            return (GroupObject)MakeGroup(kids, Matrix3x2.Identity, name, force: true);
        }

        // ---- CSS e proprietà ----

        void ParseCss(string css)
        {
            foreach (Match rule in CssRule.Matches(CssComment.Replace(css, "")))
            {
                var decls = new Dictionary<string, string>();
                Declarations(rule.Groups[2].Value, decls);
                foreach (var sel in rule.Groups[1].Value.Split(','))
                {
                    var key = sel.Trim();
                    if (key.Length == 0 || key.Any(c => c is ' ' or '>' or '+' or '~' or '[' or ':' or '#')) continue; // solo .classe e tag
                    if (!_css.TryGetValue(key, out var into)) _css[key] = into = [];
                    foreach (var (k, v) in decls) into[k] = v;
                }
            }
        }

        static void Declarations(string text, Dictionary<string, string> into)
        {
            foreach (var d in text.Split(';'))
            {
                var i = d.IndexOf(':');
                if (i > 0) into[d[..i].Trim()] = d[(i + 1)..].Replace("!important", "").Trim();
            }
        }

        // Attributi di presentazione, poi CSS, poi style="...": l'ultimo vince.
        Dictionary<string, string> Props(XElement e)
        {
            var p = new Dictionary<string, string>();
            foreach (var a in e.Attributes())
                if (!a.IsNamespaceDeclaration) p[a.Name.LocalName] = a.Value;
            void Merge(string key)
            {
                if (_css.TryGetValue(key, out var rule)) foreach (var (k, v) in rule) p[k] = v;
            }
            Merge(e.Name.LocalName);
            foreach (var c in p.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) Merge("." + c);
            if (p.TryGetValue("style", out var style)) Declarations(style, p);
            return p;
        }

        St Inherit(St s, Dictionary<string, string> p)
        {
            string? V(string k) => p.TryGetValue(k, out var v) && v != "inherit" ? v.Trim() : null;
            if (V("fill") is { } fill) s = s with { Fill = fill };
            if (V("stroke") is { } stroke) s = s with { Stroke = stroke };
            if (Len(V("stroke-width"), _vw) is { } sw) s = s with { StrokeWidth = sw };
            if (Len(V("opacity")) is { } op) s = s with { Opacity = s.Opacity * Math.Clamp(op, 0, 1) };
            if (Len(V("fill-opacity")) is { } fo) s = s with { FillOpacity = Math.Clamp(fo, 0, 1) };
            if (Len(V("stroke-opacity")) is { } so) s = s with { StrokeOpacity = Math.Clamp(so, 0, 1) };
            if (V("font-family") is { } ff) s = s with { FontFamily = ff.Split(',')[0].Trim().Trim('\'', '"') };
            if (Len(V("font-size"), 16) is { } fs && fs > 0) s = s with { FontSize = fs };
            if (V("font-weight") is { } fw) s = s with { Bold = fw is "bold" or "bolder" || int.TryParse(fw, out var n) && n >= 600 };
            if (V("font-style") is { } fst) s = s with { Italic = fst is "italic" or "oblique" };
            if (V("text-anchor") is { } ta) s = s with { Anchor = ta };
            if (V("fill-rule") == "evenodd" || V("clip-rule") == "evenodd") Note("fill-rule evenodd is not supported: holes may come out filled");
            if (V("stroke-dasharray") is { } da && da != "none") Note("dashes (stroke-dasharray) ignored");
            if (V("stroke-linecap") is "round" or "square" || V("stroke-linejoin") is "round" or "bevel") Note("stroke caps and joins (linecap, linejoin) ignored");
            foreach (var unsupported in new[] { "clip-path", "mask", "filter" })
                if (V(unsupported) is { } v && v != "none") Note($"{unsupported} ignored");
            return s;
        }

        // ---- Numeri, lunghezze, trasformazioni ----

        static double[] Nums(string? s) =>
            s is null ? [] : Number.Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();

        /// <summary>Lunghezza con unità in px; la percentuale si riferisce a <paramref name="pctBase"/>. null se assente o non valida.</summary>
        static double? Len(string? s, double pctBase = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = LengthRx.Match(s.Trim());
            if (!m.Success) return null;
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value switch
            {
                "pt" => v * 4 / 3, "pc" => v * 16, "mm" => v * 96 / 25.4, "cm" => v * 96 / 2.54, "in" => v * 96,
                "em" => v * 16, "ex" => v * 8, "%" => v * pctBase / 100, _ => v,
            };
        }

        /// <summary>Frazione: "50%" = 0.5, "0.5" = 0.5.</summary>
        static double? Frac(string? s) => s is null ? null : Len(s, 1);

        static Matrix3x2 ParseTransform(string? s)
        {
            var m = Matrix3x2.Identity;
            foreach (Match t in TransformRx.Matches(s ?? ""))
            {
                var a = Nums(t.Groups[2].Value);
                var rad = a.Length > 0 ? a[0] * Math.PI / 180 : 0;
                var x = t.Groups[1].Value switch
                {
                    "matrix" when a.Length == 6 => new Matrix3x2((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5]),
                    "translate" when a.Length >= 1 => Matrix3x2.CreateTranslation((float)a[0], a.Length > 1 ? (float)a[1] : 0),
                    "scale" when a.Length >= 1 => Matrix3x2.CreateScale((float)a[0], (float)(a.Length > 1 ? a[1] : a[0])),
                    "rotate" when a.Length == 3 => Matrix3x2.CreateRotation((float)rad, new Vector2((float)a[1], (float)a[2])),
                    "rotate" when a.Length >= 1 => Matrix3x2.CreateRotation((float)rad),
                    "skewX" when a.Length >= 1 => new Matrix3x2(1, 0, (float)Math.Tan(rad), 1, 0, 0),
                    "skewY" when a.Length >= 1 => new Matrix3x2(1, (float)Math.Tan(rad), 0, 1, 0, 0),
                    _ => Matrix3x2.Identity,
                };
                m = x * m; // "A B" applica prima B, poi A
            }
            return m;
        }

        // ---- Colori e gradienti ----

        string? Color(string raw, double alpha)
        {
            raw = raw.Trim().ToLowerInvariant();
            if (raw is "" or "none" or "transparent") return null;
            if (raw == "currentcolor") raw = "#000000";
            double r, g, b, a = 1;
            try
            {
                if (raw[0] == '#')
                {
                    var hex = raw[1..];
                    if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
                    if (hex.Length is not (6 or 8)) throw new FormatException();
                    r = Convert.ToInt32(hex[..2], 16); g = Convert.ToInt32(hex[2..4], 16); b = Convert.ToInt32(hex[4..6], 16);
                    if (hex.Length == 8) a = Convert.ToInt32(hex[6..], 16) / 255.0;
                }
                else if (raw.StartsWith("rgb"))
                {
                    var parts = raw[(raw.IndexOf('(') + 1)..raw.LastIndexOf(')')].Split([' ', ',', '/'], StringSplitOptions.RemoveEmptyEntries);
                    double C(string s) => s.EndsWith('%') ? double.Parse(s[..^1], CultureInfo.InvariantCulture) * 2.55 : double.Parse(s, CultureInfo.InvariantCulture);
                    (r, g, b) = (C(parts[0]), C(parts[1]), C(parts[2]));
                    if (parts.Length > 3) a = parts[3].EndsWith('%') ? double.Parse(parts[3][..^1], CultureInfo.InvariantCulture) / 100 : double.Parse(parts[3], CultureInfo.InvariantCulture);
                }
                else
                {
                    var named = System.Drawing.Color.FromName(raw);
                    if (!named.IsKnownColor) throw new FormatException();
                    (r, g, b) = (named.R, named.G, named.B);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
            {
                Note($"color '{raw}' not recognized, ignored");
                return null;
            }
            int R(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
            var a8 = (int)Math.Round(Math.Clamp(a * alpha, 0, 1) * 255);
            if (a8 == 0) return null;
            return a8 == 255 ? $"#{R(r):X2}{R(g):X2}{R(b):X2}" : $"#{R(r):X2}{R(g):X2}{R(b):X2}{a8:X2}";
        }

        string? Paint(string raw, double alpha, Box box)
        {
            raw = raw.Trim();
            if (raw.StartsWith("url("))
            {
                var m = UrlRx.Match(raw);
                if (m.Success && _ids.TryGetValue(m.Groups[1].Value, out var el))
                {
                    if (el.Name.LocalName is "linearGradient" or "radialGradient") return GradientPaint(el, alpha, box);
                    // Un motivo si approssima col suo colore in primo piano (l'ultimo elemento disegnato).
                    if (el.Name.LocalName == "pattern" && el.Elements().LastOrDefault() is { } fg && Props(fg).GetValueOrDefault("fill") is { } fgFill)
                    {
                        Note("pattern fills (<pattern>) approximated with a solid color");
                        return Color(fgFill, alpha);
                    }
                    Note($"fills with <{el.Name.LocalName}> are not supported and were ignored");
                }
                raw = m.Success ? m.Groups[2].Value : "none"; // colore di riserva dopo url(...)
            }
            return Color(raw, alpha);
        }

        XElement? Href(XElement el) =>
            (el.Attribute(Xlink + "href") ?? el.Attribute("href"))?.Value is { Length: > 1 } h && h[0] == '#' && _ids.TryGetValue(h[1..], out var t) ? t : null;

        string? GradAttr(XElement g, string name)
        {
            for (var i = 0; i < 10 && g != null; i++, g = Href(g)!)
                if (g.Attribute(name)?.Value is { } v) return v;
            return null;
        }

        string? GradientPaint(XElement g, double alpha, Box box)
        {
            var stopEls = new List<XElement>();
            var cur = g;
            for (var i = 0; i < 10 && cur != null && stopEls.Count == 0; i++, cur = Href(cur)!)
                stopEls = cur.Elements().Where(x => x.Name.LocalName == "stop").ToList();

            var stops = new List<GradientStop>();
            double prev = 0;
            foreach (var s in stopEls)
            {
                var sp = Props(s);
                prev = Math.Clamp(Math.Max(Frac(sp.GetValueOrDefault("offset")) ?? 0, prev), 0, 1);
                var color = Color(sp.GetValueOrDefault("stop-color", "black"), alpha * Math.Clamp(Len(sp.GetValueOrDefault("stop-opacity")) ?? 1, 0, 1)) ?? "#00000000";
                stops.Add(new GradientStop(color, prev));
            }
            if (stops.Count == 0) return null;
            if (stops.Count == 1) return stops[0].Color;

            var user = GradAttr(g, "gradientUnits") == "userSpaceOnUse";
            if (user && (box.Width < 1e-9 || box.Height < 1e-9)) return stops[^1].Color;
            var gt = ParseTransform(GradAttr(g, "gradientTransform"));
            double Coord(string name, double defFrac, bool horizontal) =>
                user ? Len(GradAttr(g, name), horizontal ? _vw : _vh) ?? defFrac * (horizontal ? _vw : _vh) : Frac(GradAttr(g, name)) ?? defFrac;
            // Dal sistema delle unità del gradiente alle frazioni del riquadro.
            (double X, double Y) ToFrac(Vector2 p) => user ? ((p.X - box.X) / box.Width, (p.Y - box.Y) / box.Height) : (p.X, p.Y);

            GradientSpec spec;
            if (g.Name.LocalName == "linearGradient")
            {
                var p1 = ToFrac(Vector2.Transform(new((float)Coord("x1", 0, true), (float)Coord("y1", 0, false)), gt));
                var p2 = ToFrac(Vector2.Transform(new((float)Coord("x2", 1, true), (float)Coord("y2", 0, false)), gt));
                spec = new GradientSpec(false, 0, false, 0.5, 0.5, stops, p1.X, p1.Y, p2.X, p2.Y);
            }
            else
            {
                var c = ToFrac(Vector2.Transform(new((float)Coord("cx", 0.5, true), (float)Coord("cy", 0.5, false)), gt));
                var r = user ? Len(GradAttr(g, "r"), Math.Min(_vw, _vh)) ?? Math.Min(_vw, _vh) / 2 : Frac(GradAttr(g, "r")) ?? 0.5;
                double rx = r * Math.Sqrt(gt.M11 * gt.M11 + gt.M12 * gt.M12), ry = r * Math.Sqrt(gt.M21 * gt.M21 + gt.M22 * gt.M22);
                if (user) (rx, ry) = (rx / box.Width, ry / box.Height);
                if (rx < 1e-6 || ry < 1e-6) return stops[^1].Color;
                spec = new GradientSpec(true, 0, false, c.X, c.Y, stops, Rx: rx, Ry: ry);
            }
            return Gradient.Format(spec);
        }

        // ---- Albero ----

        string? NameOf(Dictionary<string, string> p)
        {
            foreach (var key in new[] { "data-name", "label" })
                if (p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) && !GenericId.IsMatch(v)) return v;
            return p.TryGetValue("id", out var id) && !GenericId.IsMatch(id) ? id : null;
        }

        void Visit(XElement e, St parent, List<SceneObject> into)
        {
            var tag = e.Name.LocalName;
            if (tag is "defs" or "symbol" or "clipPath" or "mask" or "marker" or "pattern" or "linearGradient" or "radialGradient" or "style"
                or "title" or "desc" or "metadata" or "filter" or "script" or "foreignObject" || e.Name.Namespace != Svg && e.Name.Namespace != XNamespace.None)
                return;
            var p = Props(e);
            if (p.GetValueOrDefault("display") == "none" || p.GetValueOrDefault("visibility") is "hidden" or "collapse") return;
            if (_depth > MaxDepth) return;

            var st = Inherit(parent, p);
            var tf = ParseTransform(p.GetValueOrDefault("transform"));
            var name = NameOf(p);

            switch (tag)
            {
                case "g" or "a" or "svg" or "switch":
                    into.AddRange(Children(e.Elements(), st, tf, name));
                    return;
                case "use":
                    if (Href(e) is not { } target) return;
                    tf = Matrix3x2.CreateTranslation((float)(Len(p.GetValueOrDefault("x"), _vw) ?? 0), (float)(Len(p.GetValueOrDefault("y"), _vh) ?? 0)) * tf;
                    var kids = new List<SceneObject>();
                    _depth++;
                    if (target.Name.LocalName == "symbol") foreach (var c in target.Elements()) Visit(c, st, kids);
                    else Visit(target, st, kids);
                    _depth--;
                    if (kids.Count > 0) into.Add(MakeGroup(kids, tf, name));
                    return;
            }

            SceneObject? obj = tag switch
            {
                "rect" => MakeRect(p),
                "circle" => MakeEllipse(Len(p.GetValueOrDefault("cx"), _vw) ?? 0, Len(p.GetValueOrDefault("cy"), _vh) ?? 0, Len(p.GetValueOrDefault("r"), Math.Min(_vw, _vh)) ?? 0, null),
                "ellipse" => MakeEllipse(Len(p.GetValueOrDefault("cx"), _vw) ?? 0, Len(p.GetValueOrDefault("cy"), _vh) ?? 0, Len(p.GetValueOrDefault("rx"), _vw) ?? 0, Len(p.GetValueOrDefault("ry"), _vh) ?? 0),
                "path" => MakePath(p.GetValueOrDefault("d")),
                "line" => MakePath($"M {Fmt(Len(p.GetValueOrDefault("x1"), _vw))} {Fmt(Len(p.GetValueOrDefault("y1"), _vh))} L {Fmt(Len(p.GetValueOrDefault("x2"), _vw))} {Fmt(Len(p.GetValueOrDefault("y2"), _vh))}"),
                "polyline" or "polygon" => MakePoly(p.GetValueOrDefault("points"), tag == "polygon"),
                "text" => MakeText(e, p, st),
                "image" => MakeImage(p),
                _ => null,
            };
            if (obj is null) return;
            if (Finish(obj, st, p, tf, name)) into.Add(obj);
        }

        List<SceneObject> Children(IEnumerable<XElement> els, St st, Matrix3x2 tf, string? name)
        {
            var kids = new List<SceneObject>();
            _depth++;
            foreach (var c in els) Visit(c, st, kids);
            _depth--;
            return kids.Count == 0 ? [] : [MakeGroup(kids, tf, name)];
        }

        // Un gruppo con un solo figlio e senza nome non serve: il figlio assorbe la trasformazione.
        static SceneObject MakeGroup(List<SceneObject> kids, Matrix3x2 tf, string? name, bool force = false)
        {
            if (kids.Count == 1 && name is null && !force)
            {
                kids[0].Transform *= tf;
                return kids[0];
            }
            var bs = kids.Select(k => k.Bounds).ToList();
            var (x0, y0) = (bs.Min(b => b.X), bs.Min(b => b.Y));
            var box = new Box(x0, y0, bs.Max(b => b.X + b.Width) - x0, bs.Max(b => b.Y + b.Height) - y0);
            var g = new GroupObject { Name = name, Transform = tf, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
            g.Children.AddRange(kids);
            return g;
        }

        static string Fmt(double? v) => (v ?? 0).ToString("0.######", CultureInfo.InvariantCulture);

        // Colori, tratto, trasformazione e nome. false = l'oggetto non si vedrebbe.
        bool Finish(SceneObject obj, St st, Dictionary<string, string> p, Matrix3x2 tf, string? name)
        {
            obj.Transform = tf;
            obj.Name = name;
            if (obj is ImageObject) return true;
            var box = obj is TextObject t ? t.Source : new Box(obj.X, obj.Y, obj.Width, obj.Height);
            obj.Fill = Paint(st.Fill, st.Opacity * st.FillOpacity, box);
            obj.Stroke = st.StrokeWidth > 0 ? Paint(st.Stroke, st.Opacity * st.StrokeOpacity, box) : null;
            obj.StrokeWidth = obj.Stroke is null ? 0 : st.StrokeWidth;
            return obj.Fill != null || obj.Stroke != null;
        }

        // ---- Forme ----

        SceneObject? MakeRect(Dictionary<string, string> p)
        {
            double x = Len(p.GetValueOrDefault("x"), _vw) ?? 0, y = Len(p.GetValueOrDefault("y"), _vh) ?? 0;
            double w = Len(p.GetValueOrDefault("width"), _vw) ?? 0, h = Len(p.GetValueOrDefault("height"), _vh) ?? 0;
            if (w <= 0 || h <= 0) return null;
            var rx = Len(p.GetValueOrDefault("rx"), _vw); var ry = Len(p.GetValueOrDefault("ry"), _vh);
            if (rx is null or <= 0 && ry is null or <= 0) return new RectangleObject { X = x, Y = y, Width = w, Height = h };
            double a = Math.Min(rx is > 0 ? rx.Value : ry!.Value, w / 2), b = Math.Min(ry is > 0 ? ry.Value : rx!.Value, h / 2);
            string F(double v) => Fmt(v);
            return MakePath($"M {F(x + a)} {F(y)} H {F(x + w - a)} A {F(a)} {F(b)} 0 0 1 {F(x + w)} {F(y + b)} V {F(y + h - b)} A {F(a)} {F(b)} 0 0 1 {F(x + w - a)} {F(y + h)} " +
                            $"H {F(x + a)} A {F(a)} {F(b)} 0 0 1 {F(x)} {F(y + h - b)} V {F(y + b)} A {F(a)} {F(b)} 0 0 1 {F(x + a)} {F(y)} Z");
        }

        static SceneObject? MakeEllipse(double cx, double cy, double rx, double? ry)
        {
            var r2 = ry ?? rx;
            return rx > 0 && r2 > 0 ? new EllipseObject { X = cx - rx, Y = cy - r2, Width = 2 * rx, Height = 2 * r2 } : null;
        }

        SceneObject? MakePath(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return null;
            try
            {
                var segs = PathData.Parse(d);
                var box = PathData.Bounds(segs)!.Value;
                return new PathObject { Segments = segs, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
            }
            catch (CommandException)
            {
                Note("a path with invalid data was skipped");
                return null;
            }
        }

        SceneObject? MakePoly(string? points, bool close)
        {
            var n = Nums(points);
            if (n.Length < 4) return null;
            var sb = new StringBuilder();
            for (var i = 0; i + 1 < n.Length; i += 2) sb.Append(i == 0 ? "M " : " L ").Append(Fmt(n[i])).Append(' ').Append(Fmt(n[i + 1]));
            return MakePath(sb + (close ? " Z" : ""));
        }

        SceneObject? MakeText(XElement e, Dictionary<string, string> p, St st)
        {
            if (doc.TextEngine is not { } engine) { Note("texts skipped: the font engine is not available"); return null; }

            var lines = new List<string>();
            var cur = new StringBuilder();
            void Flush() { lines.Add(Regex.Replace(cur.ToString(), @"\s+", " ").Trim()); cur.Clear(); }
            void Walk(XElement el)
            {
                foreach (var n in el.Nodes())
                {
                    if (n is XText t) cur.Append(t.Value);
                    else if (n is XElement { Name.LocalName: "tspan" } c)
                    {
                        // Un tspan con y o dy nuovi inizia una nuova riga.
                        var cp = Props(c);
                        if ((cp.ContainsKey("y") || cp.ContainsKey("dy")) && cur.ToString().Trim().Length > 0) Flush();
                        Walk(c);
                    }
                }
            }
            Walk(e);
            Flush();
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) return null;

            var firstSpan = e.Elements().FirstOrDefault(c => c.Name.LocalName == "tspan");
            var sp = firstSpan is null ? [] : Props(firstSpan);
            double? First(string key) => Len((p.GetValueOrDefault(key) ?? sp.GetValueOrDefault(key))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), key == "x" ? _vw : _vh);
            double x = First("x") ?? 0, y = First("y") ?? 0;

            var family = engine.Families.FirstOrDefault(f => f.Equals(st.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? engine.DefaultFamily;
            var text = new TextObject
            {
                Text = string.Join('\n', lines), FontFamily = family, FontSize = st.FontSize, Bold = st.Bold, Italic = st.Italic,
                Align = st.Anchor switch { "middle" => "center", "end" => "right", _ => "left" }, X = x, Y = y,
            };
            text.Relayout(doc);
            // (x, y) è la linea di base della prima riga e, con text-anchor, il suo centro o la sua fine.
            text.X = x - st.Anchor switch { "middle" => text.Width / 2, "end" => text.Width, _ => 0 };
            text.Y = y - text.Lines[0].Y;
            text.Relayout(doc);
            return text;
        }

        SceneObject? MakeImage(Dictionary<string, string> p)
        {
            var href = p.GetValueOrDefault("href");
            if (href is null || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                Note("external images (not embedded as data URIs) were not imported");
                return null;
            }
            var comma = href.IndexOf(',');
            if (comma < 0 || !href[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || doc.ImageProbe is not { } probe) return null;
            byte[] data;
            try { data = Convert.FromBase64String(href[(comma + 1)..].Trim()); }
            catch (FormatException) { return null; }
            if (probe(data) is not { Width: > 0, Height: > 0 } size) { Note("an embedded image is not readable and was skipped"); return null; }

            double w = Len(p.GetValueOrDefault("width"), _vw) ?? 0, h = Len(p.GetValueOrDefault("height"), _vh) ?? 0;
            if (w <= 0 && h <= 0) (w, h) = (size.Width, size.Height);
            else if (h <= 0) h = w * size.Height / size.Width;
            else if (w <= 0) w = h * size.Width / size.Height;
            return new ImageObject
            {
                Data = data, PixelWidth = size.Width, PixelHeight = size.Height, Width = w, Height = h,
                X = Len(p.GetValueOrDefault("x"), _vw) ?? 0, Y = Len(p.GetValueOrDefault("y"), _vh) ?? 0,
            };
        }
    }
}

/// <summary>
/// Importa un file o un testo SVG come un gruppo (un solo undo). `x`, `y`, `width` e `height` indicano dove va e quanto è grande il
/// riquadro dell'illustrazione (se ne manca uno si mantiene la proporzione); senza, resta dov'è nell'SVG, in dimensione naturale.
/// </summary>
public sealed class ImportSvg : Command
{
    /// <summary>Percorso assoluto di un file .svg.</summary>
    public string? Src { get; set; }
    /// <summary>In alternativa: il testo SVG. Dopo l'applicazione viene accorciato.</summary>
    public string? Svg { get; set; }
    public string? Name { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public Placement? Rel { get; set; }
    /// <summary>Nome del layer dove creare il gruppo; senza, il layer attivo.</summary>
    public string? Layer { get; set; }

    const int MaxBytes = 20 * 1024 * 1024;

    GroupObject? _group;
    Layer? _layer;
    List<string> _notes = [];

    /// <summary>Solo in uscita: id del gruppo creato.</summary>
    public string? Id => _group?.Id;

    /// <summary>Solo in uscita: cosa dell'SVG non è stato importato.</summary>
    public List<string>? Notes => _notes.Count == 0 ? null : _notes;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _group is null ? [] : [_group.Id];

    public override void Apply(Document doc)
    {
        if (_group == null)
        {
            const string Example = "{\"cmd\":\"svg.import\",\"src\":\"/Users/me/logo.svg\",\"name\":\"logo\",\"x\":20,\"y\":20,\"width\":200}";
            if (string.IsNullOrWhiteSpace(Src) == string.IsNullOrWhiteSpace(Svg))
                throw new CommandException($"svg.import: give either 'src' (absolute path of an .svg file) or 'svg' (the SVG text), not both and not neither. Example: {Example}.");
            string text;
            if (!string.IsNullOrWhiteSpace(Src))
            {
                if (!Path.IsPathFullyQualified(Src))
                    throw new CommandException($"svg.import: 'src' must be an absolute path (e.g. \"/Users/me/logo.svg\"); got '{Src}'.");
                if (!File.Exists(Src)) throw new CommandException($"svg.import: file not found: {Src}");
                if (new FileInfo(Src).Length > MaxBytes) throw new CommandException($"svg.import: the file exceeds {MaxBytes / 1024 / 1024} MB.");
                try { text = File.ReadAllText(Src); }
                catch (IOException ex) { throw new CommandException($"svg.import: cannot read {Src}: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { throw new CommandException($"svg.import: cannot read {Src}: {ex.Message}"); }
            }
            else
            {
                if (Svg!.Length > MaxBytes) throw new CommandException($"svg.import: the SVG text exceeds {MaxBytes / 1024 / 1024} MB.");
                text = Svg;
            }
            if (Width is <= 0 || Height is <= 0) throw new CommandException("svg.import: 'width' and 'height' must be > 0.");
            if (Rel != null && (X != null || Y != null)) throw new CommandException("svg.import: with 'rel' the position is computed: remove x and y.");

            var (group, notes) = SvgFile.Build(doc, text, Name ?? (Src is null ? null : Path.GetFileNameWithoutExtension(Src)));
            var box = group.Source;
            if (Width != null || Height != null)
            {
                var k = Width != null ? Width.Value / Math.Max(box.Width, 1e-9) : Height!.Value / Math.Max(box.Height, 1e-9);
                group.Width = Width ?? box.Width * k;
                group.Height = Height ?? box.Height * k;
            }
            if (X != null) group.X = X.Value;
            if (Y != null) group.Y = Y.Value;
            if (Rel != null)
            {
                var (dx, dy) = Rel.Resolve(doc, group.Bounds);
                group.X += dx; group.Y += dy;
            }
            SvgFile.AssignIds(doc, group);
            _group = group;
            _notes = notes;
            if (Svg != null) Svg = $"(svg, {Svg.Length / 1024} KB)"; // il redo riusa l'oggetto: il testo non serve piu'
        }
        _layer ??= doc.TargetLayer(Layer);
        _layer.Objects.Add(_group);
    }

    public override void Undo(Document doc) => _layer!.Objects.Remove(_group!);
}
