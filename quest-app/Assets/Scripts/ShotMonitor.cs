using System.Collections.Generic;
using UnityEngine;

// A SIUS-style electronic scoring screen for one firing point. Draws a dark ringed target with
// plotted shots, a shot-score list and a big current-shot value onto a texture shown on the
// tablet's screen quad. Works both at runtime (AddLiveShot on each fired shot) and from an
// editor render harness (AddShot with a timestamp, then RenderAtTime each frame).
[ExecuteAlways]
public class ShotMonitor : MonoBehaviour
{
    public int Lane = 0;
    public Renderer Screen;            // the quad whose material shows the display

    const int TW = 320, TH = 208;
    static readonly Color32 BG = new Color32(16, 20, 28, 255);

    struct Shot { public float ts, score, nx, ny; }
    readonly List<Shot> shots = new List<Shot>();
    Texture2D tex; Color32[] buf;
    int lastDrawnCount = -1;
    System.Random rng;

    // ---- public API ----
    public void EnsureInit()
    {
        if (tex == null)
        {
            tex = new Texture2D(TW, TH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = "SIUS_L" + Lane };
            buf = new Color32[TW * TH];
        }
        if (rng == null) rng = new System.Random(Lane * 7919 + 13);
        if (Screen != null)
        {
            var m = Screen.sharedMaterial;
            if (m == null) { m = new Material(Shader.Find("Unlit/Texture")); Screen.sharedMaterial = m; }
            m.mainTexture = tex;
        }
    }

    public void ClearShots() { shots.Clear(); lastDrawnCount = -1; }

    public void AddShot(float score, float nx, float ny, float ts)
    { shots.Add(new Shot { ts = ts, score = score, nx = nx, ny = ny }); }

    // generate a plausible competitor shot (clusters near the ten) and add it
    public void GenShot(float ts)
    {
        double u = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble()) / 3.0; // ~triangular
        float rr = Mathf.Abs((float)(u - 0.5)) * 2f;                                // 0 (good)..1
        float score = Mathf.Clamp(10.9f - rr * 3.0f, 7.0f, 10.9f);
        float ang = (float)(rng.NextDouble() * Mathf.PI * 2);
        float radn = Mathf.Clamp((10.9f - score) / 4.0f, 0f, 0.95f);
        AddShot(score, Mathf.Cos(ang) * radn, Mathf.Sin(ang) * radn, ts);
    }

    public void AddLiveShot() { EnsureInit(); GenShot(Time.time); RenderAtTime(Time.time); }

    // Draw the state as of time t (only shots with ts <= t). Cheap: redraws only on change.
    public void RenderAtTime(float t)
    {
        EnsureInit();
        int c = 0; for (int i = 0; i < shots.Count; i++) if (shots[i].ts <= t) c++;
        if (c == lastDrawnCount) return;
        lastDrawnCount = c;
        Redraw(c);
    }

    // ---------- drawing ----------
    void Redraw(int visible)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] = BG;

        int cx = 100, cy = 106, R = 92;
        // target disc
        FillCircle(cx, cy, R + 3, new Color32(28, 32, 42, 255));
        // 10 rings; inner 4 form the black bull
        for (int ring = 1; ring <= 10; ring++)
        {
            float rr = R * (10 - ring + 1) / 10f;
            Color32 line = ring >= 7 ? new Color32(60, 70, 86, 255) : new Color32(78, 90, 108, 255);
            if (ring == 7) FillCircle(cx, cy, Mathf.RoundToInt(rr), new Color32(9, 11, 16, 255)); // black bull
            CircleOutline(cx, cy, Mathf.RoundToInt(rr), line);
        }
        // centre cross
        for (int d = -4; d <= 4; d++) { SetPx(cx + d, cy, new Color32(120, 132, 150, 255)); SetPx(cx, cy + d, new Color32(120, 132, 150, 255)); }

        // shots (oldest dim green, newest bright yellow with ring + crosshair)
        int newest = -1; float newestTs = -1e9f;
        for (int i = 0; i < shots.Count; i++) if (i < visible && shots[i].ts > newestTs) { newestTs = shots[i].ts; newest = i; }
        for (int i = 0; i < visible && i < shots.Count; i++)
        {
            int sx = cx + Mathf.RoundToInt(shots[i].nx * R);
            int sy = cy - Mathf.RoundToInt(shots[i].ny * R);
            if (i == newest) { FillCircle(sx, sy, 4, new Color32(255, 232, 80, 255)); CircleOutline(sx, sy, 6, new Color32(255, 150, 40, 255));
                               for (int d = -7; d <= 7; d++) { SetPx(sx + d, sy, new Color32(255, 190, 60, 255)); SetPx(sx, sy + d, new Color32(255, 190, 60, 255)); } }
            else FillCircle(sx, sy, 3, new Color32(90, 220, 120, 255));
        }

        // ---------- right panel ----------
        int px = 206;
        VLine(px - 6, 6, TH - 6, new Color32(40, 48, 60, 255));
        Text(px, 8, "SIUS", 2, new Color32(90, 190, 255, 255));
        Text(px + 70, 10, "L" + Lane, 1, new Color32(170, 180, 195, 255));
        HLine(px - 2, TW - 6, 30, new Color32(40, 48, 60, 255));

        // shot list (last 7)
        Text(px, 36, "SH VAL", 1, new Color32(120, 132, 150, 255));
        int shown = 0;
        for (int i = visible - 1; i >= 0 && shown < 7; i--, shown++)
        {
            int yy = 48 + shown * 13;
            Color32 col = (i == newest) ? new Color32(255, 232, 80, 255) : new Color32(150, 230, 170, 255);
            Text(px, yy, Pad2(i + 1), 1, new Color32(110, 122, 140, 255));
            Text(px + 26, yy, Fmt(shots[i].score), 1, col);
        }

        // series total + count
        float tot = 0; for (int i = 0; i < visible && i < shots.Count; i++) tot += shots[i].score;
        HLine(px - 2, TW - 6, 150, new Color32(40, 48, 60, 255));
        Text(px, 156, "N " + visible, 1, new Color32(150, 162, 180, 255));
        Text(px, 168, "T" + FmtTot(tot), 1, new Color32(150, 162, 180, 255));

        // big current value (bottom)
        string big = newest >= 0 ? Fmt(shots[newest].score) : "---";
        Text(px + 2, 182, big, 3, new Color32(120, 255, 150, 255));

        tex.SetPixels32(buf); tex.Apply(false);
    }

    // ---------- primitives ----------
    // y is top-origin here; flip to Texture2D's bottom-origin so text/layout render upright
    void SetPx(int x, int y, Color32 c) { if (x < 0 || y < 0 || x >= TW || y >= TH) return; buf[(TH - 1 - y) * TW + x] = c; }
    void FillCircle(int cx, int cy, int r, Color32 c)
    { for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) if (x * x + y * y <= r * r) SetPx(cx + x, cy + y, c); }
    void CircleOutline(int cx, int cy, int r, Color32 c)
    { for (int a = 0; a < 360; a += 2) { float rad = a * Mathf.Deg2Rad; SetPx(cx + Mathf.RoundToInt(Mathf.Cos(rad) * r), cy + Mathf.RoundToInt(Mathf.Sin(rad) * r), c); } }
    void HLine(int x0, int x1, int y, Color32 c) { for (int x = x0; x <= x1; x++) SetPx(x, y, c); }
    void VLine(int x, int y0, int y1, Color32 c) { for (int y = y0; y <= y1; y++) SetPx(x, y, c); }

    static string Pad2(int n) { return n < 10 ? "0" + n : n.ToString(); }
    static string Fmt(float s) { int w = Mathf.FloorToInt(s); int d = Mathf.RoundToInt((s - w) * 10f); if (d == 10) { w++; d = 0; } return w + "." + d; }
    static string FmtTot(float s) { int w = Mathf.FloorToInt(s); int d = Mathf.RoundToInt((s - w) * 10f); if (d == 10) { w++; d = 0; } return w + "." + d; }

    // ---------- 5x7 bitmap font ----------
    static Dictionary<char, byte[]> FONT;
    static void InitFont()
    {
        if (FONT != null) return;
        var g = new Dictionary<char, string[]>
        {
            ['0'] = new[]{"01110","10001","10011","10101","11001","10001","01110"},
            ['1'] = new[]{"00100","01100","00100","00100","00100","00100","01110"},
            ['2'] = new[]{"01110","10001","00001","00010","00100","01000","11111"},
            ['3'] = new[]{"11111","00010","00100","00010","00001","10001","01110"},
            ['4'] = new[]{"00010","00110","01010","10010","11111","00010","00010"},
            ['5'] = new[]{"11111","10000","11110","00001","00001","10001","01110"},
            ['6'] = new[]{"00110","01000","10000","11110","10001","10001","01110"},
            ['7'] = new[]{"11111","00001","00010","00100","01000","01000","01000"},
            ['8'] = new[]{"01110","10001","10001","01110","10001","10001","01110"},
            ['9'] = new[]{"01110","10001","10001","01111","00001","00010","01100"},
            ['.'] = new[]{"00000","00000","00000","00000","00000","00110","00110"},
            [':'] = new[]{"00000","01100","01100","00000","01100","01100","00000"},
            ['-'] = new[]{"00000","00000","00000","11111","00000","00000","00000"},
            [' '] = new[]{"00000","00000","00000","00000","00000","00000","00000"},
            ['S'] = new[]{"01111","10000","10000","01110","00001","00001","11110"},
            ['I'] = new[]{"01110","00100","00100","00100","00100","00100","01110"},
            ['U'] = new[]{"10001","10001","10001","10001","10001","10001","01110"},
            ['L'] = new[]{"10000","10000","10000","10000","10000","10000","11111"},
            ['A'] = new[]{"01110","10001","10001","11111","10001","10001","10001"},
            ['N'] = new[]{"10001","11001","10101","10101","10011","10001","10001"},
            ['E'] = new[]{"11111","10000","10000","11110","10000","10000","11111"},
            ['H'] = new[]{"10001","10001","10001","11111","10001","10001","10001"},
            ['O'] = new[]{"01110","10001","10001","10001","10001","10001","01110"},
            ['T'] = new[]{"11111","00100","00100","00100","00100","00100","00100"},
            ['V'] = new[]{"10001","10001","10001","10001","10001","01010","00100"},
        };
        FONT = new Dictionary<char, byte[]>();
        foreach (var kv in g)
        {
            var rows = new byte[7];
            for (int r = 0; r < 7; r++) { byte b = 0; for (int c = 0; c < 5; c++) if (kv.Value[r][c] == '1') b |= (byte)(1 << (4 - c)); rows[r] = b; }
            FONT[kv.Key] = rows;
        }
    }

    void Text(int x, int y, string s, int scale, Color32 col)
    {
        InitFont();
        int cxp = x;
        foreach (char ch in s.ToUpper())
        {
            byte[] rows; if (!FONT.TryGetValue(ch, out rows)) { cxp += 6 * scale; continue; }
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 5; c++)
                    if ((rows[r] & (1 << (4 - c))) != 0)
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                                SetPx(cxp + c * scale + dx, y + r * scale + dy, col);
            cxp += 6 * scale;
        }
    }

    void Start()  { if (Application.isPlaying) { EnsureInit(); RenderAtTime(Time.time); } }
}
