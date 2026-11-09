// 描く部品。ゲームの世界は ESP32 版と同じ 240x320 の座標で、それを 3 倍 (720x960) の点で描く。
// 色は 0xRRGGBB、a は不透明さ (0〜255。255 で上書き)。描く先 (Surf) は画面でも、取っておく背景の絵でも同じに扱う。
//
// 部品は 2 つの系統がある:
//  - ESP32 版と同じ決まりの部品 (R・P・L・LT・C・CR・poly・T)。背景をそのまま移すのに使う。
//    座標 (x, y) は液晶の点 1 つ (3x3 の点) の左上で、円や線はその点の真ん中を通る
//  - 細かく描くための部品 (Disc・Ring・Seg・Poly・Ball・Rod・Q など)。座標はそのまま連続した位置で、1/3 の細かさまで描ける。
//    戦車・敵・ボスなどの絵はこちらで描く
using System;
using System.Collections.Generic;

namespace Foundry
{
  public struct Pt
  {
    public float x, y;
    public Pt(float x, float y)
    {
      this.x = x;
      this.y = y;
    }
  }

  public class Surf
  {
    public int[] Buf;
    public int Stride = D.PW;  // 1 行の点の数
    public int Base;           // Buf の中で、行 Y0 が始まる所
    public int Y0, Hpx;        // この面が持つ行 (点の座標で Y0 〜 Y0+Hpx-1)
    public float OX, OY;       // 描くときに足すずらし (世界の座標)
    public int CX0 = 0, CY0 = -1000000, CX1 = D.PW - 1, CY1 = 1000000; // 描ける範囲 (点の座標。端も含む)
    public bool Keyed;         // 抜き色あり (0 = 透明)。半透明の描き込みは透明の所を塗らない
    public Surf Alt;           // 半透明の描き込みはこちらへ回す (手前の絵の影を床に落とすため)
    // 重ねても濃くならない半透明 (影など) を描くための覚え
    public byte[] Mask;
    public bool MaskOn;
    public int MX0, MY0, MX1, MY1;
  }

  public class D
  {
    public const int S = 3;
    public const int W = 240, H = 320, PW = W * S, PH = H * S;
    public const float PIf = 3.14159265f;
    public const float PX = 1f / S; // 細かい点 1 つの大きさ (世界の座標で)
    public static Surf Sf;

    // ---- 計算 ----
    protected static float sinf(float a) { return (float)Math.Sin(a); }
    protected static float cosf(float a) { return (float)Math.Cos(a); }
    protected static float tanhf(float a) { return (float)Math.Tanh(a); }
    protected static float sqrtf(float a) { return (float)Math.Sqrt(a); }
    protected static float fabsf(float a) { return Math.Abs(a); }
    protected static float atan2f(float y, float x) { return (float)Math.Atan2(y, x); }
    protected static float asinf(float a) { return (float)Math.Asin(a); }
    protected static float acosf(float a) { return (float)Math.Acos(a); }
    protected static float hypotf(float x, float y) { return (float)Math.Sqrt(x * x + y * y); }
    protected static float floorf(float a) { return (float)Math.Floor(a); }
    protected static float ceilf(float a) { return (float)Math.Ceiling(a); }
    protected static float expf(float a) { return (float)Math.Exp(a); }
    protected static float powf(float a, float b) { return (float)Math.Pow(a, b); }
    protected static float fmodf(float a, float b) { return a % b; }
    protected static float fmod1(float v) { return v - (float)Math.Floor(v); }
    protected static int lroundf(float v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
    protected static int min(int a, int b) { return a < b ? a : b; }
    protected static int max(int a, int b) { return a > b ? a : b; }
    protected static float min(float a, float b) { return a < b ? a : b; }
    protected static float max(float a, float b) { return a > b ? a : b; }
    protected static float clampf(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
    protected static int clampi(int v, int a, int b) { return v < a ? a : v > b ? b : v; }
    protected static float constrain(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
    protected static int A(float a) { return lroundf(clampf(a, 0, 1) * 255); }
    protected static float angDiff(float a, float b) { return fmod1((a - b + PIf * 3) / (PIf * 2)) * PIf * 2 - PIf; }

    // 0〜1 の決まった乱数
    public static float hs(float n)
    {
      uint h = unchecked((uint)(long)Math.Round(n * 1024.0f)) * 0x9E3779B1u;
      h ^= h >> 15;
      h *= 0x85EBCA77u;
      h ^= h >> 13;
      h *= 0xC2B2AE3Du;
      h ^= h >> 16;
      return (h >> 8) * (1.0f / 16777216.0f);
    }
    public static float h2(float x, float y)
    {
      uint h = unchecked((uint)(long)Math.Round(x * 16.0f) * 0x9E3779B1u ^ (uint)(long)Math.Round(y * 16.0f) * 0x85EBCA77u);
      h ^= h >> 15;
      h *= 0x2C1B3C6Du;
      h ^= h >> 12;
      h *= 0x297A2D39u;
      h ^= h >> 15;
      return (h >> 8) * (1.0f / 16777216.0f);
    }
    // なめらかなノイズ (0〜1)
    public static float vn(float x, float y)
    {
      float xi = (float)Math.Floor(x), yi = (float)Math.Floor(y), xf = x - xi, yf = y - yi;
      float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
      float a = h2(xi, yi), b = h2(xi + 1, yi), c = h2(xi, yi + 1), d = h2(xi + 1, yi + 1);
      return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    // ---- 色 ----
    public static uint Mix(uint c0, uint c1, float k)
    {
      k = clampf(k, 0, 1);
      int r = (int)(((c0 >> 16) & 255) * (1 - k) + ((c1 >> 16) & 255) * k + 0.5f);
      int g = (int)(((c0 >> 8) & 255) * (1 - k) + ((c1 >> 8) & 255) * k + 0.5f);
      int b = (int)((c0 & 255) * (1 - k) + (c1 & 255) * k + 0.5f);
      return (uint)((r << 16) | (g << 8) | b);
    }
    public static uint Mul(uint c, float k)
    {
      int r = clampi((int)(((c >> 16) & 255) * k + 0.5f), 0, 255);
      int g = clampi((int)(((c >> 8) & 255) * k + 0.5f), 0, 255);
      int b = clampi((int)((c & 255) * k + 0.5f), 0, 255);
      return (uint)((r << 16) | (g << 8) | b);
    }
    // 暗い色から明るい色への段 (n 段)。陰影をつけるのに使う
    public static uint[] Ramp(params uint[] cols) { return cols; }

    static int Blend(int dst, uint c, int a)
    {
      int na = 255 - a;
      int r = (((dst >> 16) & 255) * na + (int)((c >> 16) & 255) * a + 127) / 255;
      int g = (((dst >> 8) & 255) * na + (int)((c >> 8) & 255) * a + 127) / 255;
      int b = ((dst & 255) * na + (int)(c & 255) * a + 127) / 255;
      return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    // ---- 点の座標で 1 行を塗る ----
    static void SpanTo(Surf s, int x0, int x1, int y, uint c, int a)
    {
      if (y < s.CY0 || y > s.CY1)
        return;
      int ry = y - s.Y0;
      if (ry < 0 || ry >= s.Hpx)
        return;
      if (x0 < s.CX0)
        x0 = s.CX0;
      if (x1 > s.CX1)
        x1 = s.CX1;
      if (x0 > x1)
        return;
      int[] b = s.Buf;
      int i = s.Base + ry * s.Stride + x0, e = i + (x1 - x0);
      if (a >= 255)
      {
        int v = unchecked((int)(0xFF000000u | c));
        for (; i <= e; i++)
          b[i] = v;
      }
      else if (a > 0)
      {
        if (s.Keyed)
        {
          for (; i <= e; i++)
            if (b[i] != 0)
              b[i] = Blend(b[i], c, a);
        }
        else
          for (; i <= e; i++)
            b[i] = Blend(b[i], c, a);
      }
    }

    public static void SpanPx(int x0, int x1, int y, uint c, int a)
    {
      Surf s = Sf;
      if (s.MaskOn)
      {
        if (y < s.CY0 || y > s.CY1)
          return;
        int ry = y - s.Y0;
        if (ry < 0 || ry >= s.Hpx)
          return;
        if (x0 < s.CX0)
          x0 = s.CX0;
        if (x1 > s.CX1)
          x1 = s.CX1;
        if (x0 > x1)
          return;
        byte av = (byte)clampi(a, 0, 255);
        int i = ry * s.Stride;
        for (int x = x0; x <= x1; x++)
          if (s.Mask[i + x] < av)
            s.Mask[i + x] = av;
        if (x0 < s.MX0) s.MX0 = x0;
        if (x1 > s.MX1) s.MX1 = x1;
        if (ry < s.MY0) s.MY0 = ry;
        if (ry > s.MY1) s.MY1 = ry;
        return;
      }
      if (a < 255 && s.Alt != null)
        s = s.Alt;
      SpanTo(s, x0, x1, y, c, a);
    }

    // 重ねても濃くならない半透明: BeginMask のあと描いたものは覚えるだけにして、EndMask で 1 度だけ c で塗る
    public static void BeginMask()
    {
      Surf s = Sf;
      if (s.Mask == null || s.Mask.Length < s.Stride * s.Hpx)
        s.Mask = new byte[s.Stride * s.Hpx];
      s.MaskOn = true;
      s.MX0 = s.MY0 = int.MaxValue;
      s.MX1 = s.MY1 = int.MinValue;
    }
    public static void EndMask(uint c, int a = 255)
    {
      Surf s = Sf;
      s.MaskOn = false;
      if (s.MX1 < s.MX0)
        return;
      Surf dst = s.Alt != null ? s.Alt : s;
      for (int ry = s.MY0; ry <= s.MY1; ry++)
      {
        int row = ry * s.Stride;
        for (int x = s.MX0; x <= s.MX1; x++)
        {
          int m = s.Mask[row + x];
          if (m == 0)
            continue;
          s.Mask[row + x] = 0;
          SpanTo(dst, x, x, ry + s.Y0, c, m * a / 255);
        }
      }
    }

    // ---- 多角形・円 (点の座標。点の真ん中で決める) ----
    // 凸な多角形
    public static void PolyPx(float[] xs, float[] ys, int n, uint c, int a)
    {
      float mn = 1e9f, mx = -1e9f;
      for (int i = 0; i < n; i++)
      {
        if (ys[i] < mn) mn = ys[i];
        if (ys[i] > mx) mx = ys[i];
      }
      int y0 = (int)Math.Ceiling(mn - 0.5f), y1 = (int)Math.Floor(mx - 0.5f);
      for (int y = y0; y <= y1; y++)
      {
        float yc = y + 0.5f, lo = 1e9f, hi = -1e9f;
        for (int i = 0; i < n; i++)
        {
          int j = (i + 1) % n;
          float ay = ys[i], by = ys[j];
          if (ay == by)
            continue;
          if ((yc < ay && yc < by) || (yc > ay && yc > by))
            continue;
          float x = xs[i] + (xs[j] - xs[i]) * (yc - ay) / (by - ay);
          if (x < lo) lo = x;
          if (x > hi) hi = x;
        }
        if (hi >= lo)
        {
          int x0 = (int)Math.Ceiling(lo - 0.5f), x1 = (int)Math.Floor(hi - 0.5f);
          if (x1 >= x0)
            SpanPx(x0, x1, y, c, a);
        }
      }
    }

    public static void DiscPx(float X, float Y, float Rr, uint c, int a)
    {
      if (Rr <= 0)
        return;
      int y0 = (int)Math.Ceiling(Y - Rr - 0.5f), y1 = (int)Math.Floor(Y + Rr - 0.5f);
      float r2 = Rr * Rr;
      for (int y = y0; y <= y1; y++)
      {
        float dy = y + 0.5f - Y, w2 = r2 - dy * dy;
        if (w2 < 0)
          continue;
        float hw = (float)Math.Sqrt(w2);
        int x0 = (int)Math.Ceiling(X - hw - 0.5f), x1 = (int)Math.Floor(X + hw - 0.5f);
        if (x1 >= x0)
          SpanPx(x0, x1, y, c, a);
      }
    }

    public static void RingPx(float X, float Y, float R0, float R1, uint c, int a)
    {
      if (R1 <= 0)
        return;
      if (R0 <= 0)
      {
        DiscPx(X, Y, R1, c, a);
        return;
      }
      int y0 = (int)Math.Ceiling(Y - R1 - 0.5f), y1 = (int)Math.Floor(Y + R1 - 0.5f);
      for (int y = y0; y <= y1; y++)
      {
        float dy = y + 0.5f - Y, w2 = R1 * R1 - dy * dy;
        if (w2 < 0)
          continue;
        float hw = (float)Math.Sqrt(w2);
        int ox0 = (int)Math.Ceiling(X - hw - 0.5f), ox1 = (int)Math.Floor(X + hw - 0.5f);
        float i2 = R0 * R0 - dy * dy;
        if (i2 <= 0)
        {
          if (ox1 >= ox0)
            SpanPx(ox0, ox1, y, c, a);
          continue;
        }
        float ih = (float)Math.Sqrt(i2);
        int ix0 = (int)Math.Ceiling(X - ih - 0.5f), ix1 = (int)Math.Floor(X + ih - 0.5f);
        if (ix0 - 1 >= ox0)
          SpanPx(ox0, ix0 - 1, y, c, a);
        if (ox1 >= ix1 + 1)
          SpanPx(ix1 + 1, ox1, y, c, a);
      }
    }

    // ================= ESP32 版と同じ決まりの部品 =================
    static int RI(float v) { return (int)Math.Floor(v + 0.5f); }

    public static void span(int x0, int x1, int y, uint c, int a = 255) { R(x0, y, x1 - x0 + 1, 1, c, a); }

    public static void R(float x, float y, float w, float h, uint c, int a = 255)
    {
      x += Sf.OX;
      y += Sf.OY;
      int X0 = RI(x * S), X1 = RI((x + w) * S) - 1, Y0 = RI(y * S), Y1 = RI((y + h) * S) - 1;
      if (X1 < X0)
        return;
      int lo = Math.Max(Y0, Math.Max(Sf.CY0, Sf.Y0)), hi = Math.Min(Y1, Math.Min(Sf.CY1, Sf.Y0 + Sf.Hpx - 1));
      for (int yy = lo; yy <= hi; yy++)
        SpanPx(X0, X1, yy, c, a);
    }

    public static void P(float x, float y, uint c, int a = 255) { R(x, y, 1, 1, c, a); }

    public static void L(float x0, float y0, float x1, float y1, uint c, int a = 255) { LT(x0, y0, x1, y1, 1, c, a); }

    // 太さ t の線 (ESP32 版は 1 点の線を t 本並べていた。急な線は横に、ゆるい線は縦に並べる)
    public static void LT(float x0, float y0, float x1, float y1, float t, uint c, int a = 255)
    {
      bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
      float sh = ((int)Math.Round(t) % 2 == 0) ? 0.5f : 0; // 偶数の太さは半点ずれる
      float ax = x0 + 0.5f + Sf.OX, ay = y0 + 0.5f + Sf.OY, bx = x1 + 0.5f + Sf.OX, by = y1 + 0.5f + Sf.OY, h = t / 2;
      float[] xs = new float[4], ys = new float[4];
      if (steep)
      {
        float s = by >= ay ? 0.5f : -0.5f;
        ay -= s;
        by += s;
        ax += sh;
        bx += sh;
        xs[0] = ax - h; ys[0] = ay;
        xs[1] = ax + h; ys[1] = ay;
        xs[2] = bx + h; ys[2] = by;
        xs[3] = bx - h; ys[3] = by;
      }
      else
      {
        float s = bx >= ax ? 0.5f : -0.5f;
        ax -= s;
        bx += s;
        ay += sh;
        by += sh;
        xs[0] = ax; ys[0] = ay - h;
        xs[1] = bx; ys[1] = by - h;
        xs[2] = bx; ys[2] = by + h;
        xs[3] = ax; ys[3] = ay + h;
      }
      for (int i = 0; i < 4; i++)
      {
        xs[i] *= S;
        ys[i] *= S;
      }
      PolyPx(xs, ys, 4, c, a);
    }

    public static void C(float cx, float cy, float r, uint c, int a = 255)
    {
      if (r < 0)
        return;
      DiscPx((cx + 0.5f + Sf.OX) * S, (cy + 0.5f + Sf.OY) * S, (r + 0.45f) * S, c, a);
    }

    public static void CR(float cx, float cy, float r, uint c, int a = 255)
    {
      RingPx((cx + 0.5f + Sf.OX) * S, (cy + 0.5f + Sf.OY) * S, (r - 0.5f) * S, (r + 0.5f) * S, c, a);
    }

    // 凸な多角形 (ESP32 版と同じく、点の半分ずつ太らせて塗る)
    public static void poly(Pt[] p, uint c, int a = 255)
    {
      int n = p.Length;
      float mn = 1e9f, mx = -1e9f;
      for (int i = 0; i < n; i++)
      {
        mn = Math.Min(mn, p[i].y);
        mx = Math.Max(mx, p[i].y);
      }
      int Y0 = (int)Math.Ceiling(((mn - 0.5f) + 0.5f + Sf.OY) * S - 0.5f), Y1 = (int)Math.Floor(((mx + 0.5f) + 0.5f + Sf.OY) * S - 0.5f);
      for (int py = Y0; py <= Y1; py++)
      {
        float y = (py + 0.5f) / S - Sf.OY - 0.5f; // 世界の座標 (点の真ん中の決まり)
        float lo = 1e9f, hi = -1e9f;
        for (int i = 0; i < n; i++)
        {
          Pt A = p[i], B = p[(i + 1) % n];
          float ya = Math.Min(A.y, B.y), yb = Math.Max(A.y, B.y);
          if (y < ya - 0.5f || y > yb + 0.5f)
            continue;
          if (A.y == B.y)
          {
            lo = Math.Min(lo, Math.Min(A.x, B.x));
            hi = Math.Max(hi, Math.Max(A.x, B.x));
          }
          else
          {
            float k = clampf((y - A.y) / (B.y - A.y), 0, 1);
            float x = A.x + (B.x - A.x) * k;
            lo = Math.Min(lo, x);
            hi = Math.Max(hi, x);
          }
        }
        if (hi >= lo)
        {
          int x0 = (int)Math.Ceiling((lo - 0.5f + 0.5f + Sf.OX) * S - 0.5f), x1 = (int)Math.Floor((hi + 0.5f + 0.5f + Sf.OX) * S - 0.5f);
          if (x1 >= x0)
            SpanPx(x0, x1, py, c, a);
        }
      }
    }

    public static void T(float x0, float y0, float x1, float y1, float x2, float y2, uint c, int a = 255)
    {
      poly(new[] { new Pt(x0, y0), new Pt(x1, y1), new Pt(x2, y2) }, c, a);
    }

    // 道のりを step ごとの点に分ける
    public static List<Pt> pathPts(List<Pt> segs, float step = 1.5f)
    {
      List<Pt> o = new List<Pt>();
      for (int i = 0; i + 1 < segs.Count; i++)
      {
        Pt a = segs[i], b = segs[i + 1];
        float l = hypotf(b.x - a.x, b.y - a.y);
        int n = Math.Max(1, (int)Math.Ceiling(l / step));
        for (int k = 0; k < n; k++)
          o.Add(new Pt(a.x + (b.x - a.x) * k / n, a.y + (b.y - a.y) * k / n));
      }
      if (segs.Count > 0)
        o.Add(segs[segs.Count - 1]);
      return o;
    }
    public static void arc(List<Pt> o, float cx, float cy, float r, float a0, float a1, int n = 24)
    {
      for (int k = 0; k <= n; k++)
      {
        float a = a0 + (a1 - a0) * k / n;
        o.Add(new Pt(cx + cosf(a) * r, cy + sinf(a) * r));
      }
    }
    public static List<Pt> Pts(params float[] xy)
    {
      List<Pt> o = new List<Pt>();
      for (int i = 0; i + 1 < xy.Length; i += 2)
        o.Add(new Pt(xy[i], xy[i + 1]));
      return o;
    }

    // ================= 細かく描くための部品 (座標はそのまま) =================
    // 細かい点 1 つ
    public static void Q(float x, float y, uint c, int a = 255)
    {
      int X = (int)Math.Floor((x + Sf.OX) * S), Y = (int)Math.Floor((y + Sf.OY) * S);
      SpanPx(X, X, Y, c, a);
    }
    // 四角 (R と同じ。細かい大きさも使える)
    public static void Rect(float x, float y, float w, float h, uint c, int a = 255) { R(x, y, w, h, c, a); }

    public static void Disc(float cx, float cy, float r, uint c, int a = 255)
    {
      DiscPx((cx + Sf.OX) * S, (cy + Sf.OY) * S, r * S, c, a);
    }
    public static void Ring(float cx, float cy, float r, float w, uint c, int a = 255)
    {
      RingPx((cx + Sf.OX) * S, (cy + Sf.OY) * S, (r - w / 2) * S, (r + w / 2) * S, c, a);
    }
    public static void Ell(float cx, float cy, float rx, float ry, uint c, int a = 255)
    {
      if (rx <= 0 || ry <= 0)
        return;
      float X = (cx + Sf.OX) * S, Y = (cy + Sf.OY) * S, RX = rx * S, RY = ry * S;
      int y0 = (int)Math.Ceiling(Y - RY - 0.5f), y1 = (int)Math.Floor(Y + RY - 0.5f);
      for (int y = y0; y <= y1; y++)
      {
        float dy = (y + 0.5f - Y) / RY, w2 = 1 - dy * dy;
        if (w2 < 0)
          continue;
        float hw = (float)Math.Sqrt(w2) * RX;
        int x0 = (int)Math.Ceiling(X - hw - 0.5f), x1 = (int)Math.Floor(X + hw - 0.5f);
        if (x1 >= x0)
          SpanPx(x0, x1, y, c, a);
      }
    }
    public static void Poly(uint c, int a, params float[] xy)
    {
      int n = xy.Length / 2;
      float[] xs = new float[n], ys = new float[n];
      for (int i = 0; i < n; i++)
      {
        xs[i] = (xy[i * 2] + Sf.OX) * S;
        ys[i] = (xy[i * 2 + 1] + Sf.OY) * S;
      }
      PolyPx(xs, ys, n, c, a);
    }
    public static void Tri(float x0, float y0, float x1, float y1, float x2, float y2, uint c, int a = 255) { Poly(c, a, x0, y0, x1, y1, x2, y2); }

    // 細い線 (点 1 つの太さ)
    public static void Line1(float x0, float y0, float x1, float y1, uint c, int a = 255)
    {
      int X0 = (int)Math.Floor((x0 + Sf.OX) * S), Y0 = (int)Math.Floor((y0 + Sf.OY) * S), X1 = (int)Math.Floor((x1 + Sf.OX) * S), Y1 = (int)Math.Floor((y1 + Sf.OY) * S);
      int dx = Math.Abs(X1 - X0), sx = X0 < X1 ? 1 : -1, dy = -Math.Abs(Y1 - Y0), sy = Y0 < Y1 ? 1 : -1, e = dx + dy;
      for (int n = 0; n < 4000; n++)
      {
        SpanPx(X0, X0, Y0, c, a);
        if (X0 == X1 && Y0 == Y1)
          break;
        int e2 = 2 * e;
        if (e2 >= dy)
        {
          e += dy;
          X0 += sx;
        }
        if (e2 <= dx)
        {
          e += dx;
          Y0 += sy;
        }
      }
    }

    // 太さ w の線 (端は丸い)。w が点 1 つぶん以下なら細い線
    public static void Seg(float x0, float y0, float x1, float y1, float w, uint c, int a = 255)
    {
      if (w * S <= 1.2f)
      {
        Line1(x0, y0, x1, y1, c, a);
        return;
      }
      float ax = (x0 + Sf.OX) * S, ay = (y0 + Sf.OY) * S, bx = (x1 + Sf.OX) * S, by = (y1 + Sf.OY) * S, r = w * S / 2;
      float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
      int px0 = (int)Math.Floor(Math.Min(ax, bx) - r), px1 = (int)Math.Ceiling(Math.Max(ax, bx) + r);
      int py0 = (int)Math.Floor(Math.Min(ay, by) - r), py1 = (int)Math.Ceiling(Math.Max(ay, by) + r);
      float r2 = r * r;
      for (int y = py0; y <= py1; y++)
      {
        int run = int.MinValue;
        for (int x = px0; x <= px1 + 1; x++)
        {
          bool inside = false;
          if (x <= px1)
          {
            float qx = x + 0.5f - ax, qy = y + 0.5f - ay;
            float t = l2 > 0 ? clampf((qx * dx + qy * dy) / l2, 0, 1) : 0;
            float ex = qx - dx * t, ey = qy - dy * t;
            inside = ex * ex + ey * ey <= r2;
          }
          if (inside && run == int.MinValue)
            run = x;
          else if (!inside && run != int.MinValue)
          {
            SpanPx(run, x - 1, y, c, a);
            run = int.MinValue;
          }
        }
      }
    }

    // ---- 陰影 (左上からの光。段のあいだはディザで混ぜる) ----
    static readonly float[] BAYER = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    const float LX = -0.5f, LY = -0.62f, LZ = 0.6f;
    static readonly float HX, HY, HZ; // 照り返しの向き
    static D()
    {
      float hx = LX, hy = LY, hz = LZ + 1, l = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
      HX = hx / l;
      HY = hy / l;
      HZ = hz / l;
    }
    protected static float Bayer(int x, int y) { return (BAYER[(y & 3) * 4 + (x & 3)] + 0.5f) / 16; }
    static uint Pick(uint[] ramp, float I, int x, int y)
    {
      int n = ramp.Length;
      float f = clampf(I, 0, 1) * (n - 1);
      int i = (int)Math.Floor(f);
      if (f - i > Bayer(x, y))
        i++;
      return ramp[clampi(i, 0, n - 1)];
    }
    static float Lit(float nx, float ny, float nz, float amb, float spec)
    {
      float d = nx * LX + ny * LY + nz * LZ;
      float I = amb + (1 - amb) * Math.Max(0, d);
      if (spec > 0)
      {
        float h = nx * HX + ny * HY + nz * HZ;
        if (h > 0)
          I += spec * (float)Math.Pow(h, 24);
      }
      return I;
    }

    // 陰影のついた球 (ramp は暗い→明るい)。outline なら縁を ramp[0] で囲む
    public static void Ball(float cx, float cy, float r, uint[] ramp, float spec = 0.5f, bool outline = true, float amb = 0.15f)
    {
      float X = (cx + Sf.OX) * S, Y = (cy + Sf.OY) * S, Rp = r * S;
      if (Rp <= 0.5f)
        return;
      int y0 = (int)Math.Floor(Y - Rp), y1 = (int)Math.Ceiling(Y + Rp), x0 = (int)Math.Floor(X - Rp), x1 = (int)Math.Ceiling(X + Rp);
      float edge = outline ? 1 - 1.2f / Rp : 2;
      for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
          float dx = (x + 0.5f - X) / Rp, dy = (y + 0.5f - Y) / Rp, d2 = dx * dx + dy * dy;
          if (d2 > 1)
            continue;
          uint c;
          if (d2 > edge * edge)
            c = ramp[0];
          else
          {
            float nz = (float)Math.Sqrt(1 - d2);
            c = Pick(ramp, Lit(dx, dy, nz, amb, spec), x, y);
          }
          SpanPx(x, x, y, c, 255);
        }
    }

    // 陰影のついた棒 (筒。端は丸い)
    public static void Rod(float x0, float y0, float x1, float y1, float r, uint[] ramp, float spec = 0.45f, bool outline = true, float amb = 0.15f)
    {
      float ax = (x0 + Sf.OX) * S, ay = (y0 + Sf.OY) * S, bx = (x1 + Sf.OX) * S, by = (y1 + Sf.OY) * S, Rp = r * S;
      if (Rp <= 0.5f)
      {
        Line1(x0, y0, x1, y1, ramp[ramp.Length / 2]);
        return;
      }
      float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
      int px0 = (int)Math.Floor(Math.Min(ax, bx) - Rp), px1 = (int)Math.Ceiling(Math.Max(ax, bx) + Rp);
      int py0 = (int)Math.Floor(Math.Min(ay, by) - Rp), py1 = (int)Math.Ceiling(Math.Max(ay, by) + Rp);
      float edge = outline ? 1 - 1.2f / Rp : 2;
      for (int y = py0; y <= py1; y++)
        for (int x = px0; x <= px1; x++)
        {
          float qx = x + 0.5f - ax, qy = y + 0.5f - ay;
          float t = l2 > 0 ? clampf((qx * dx + qy * dy) / l2, 0, 1) : 0;
          float ex = (qx - dx * t) / Rp, ey = (qy - dy * t) / Rp, d2 = ex * ex + ey * ey;
          if (d2 > 1)
            continue;
          uint c;
          if (d2 > edge * edge)
            c = ramp[0];
          else
            c = Pick(ramp, Lit(ex, ey, (float)Math.Sqrt(1 - d2), amb, spec), x, y);
          SpanPx(x, x, y, c, 255);
        }
    }

    // 陰影のついた板 (上と左が明るく、下と右が暗い。中は上から下へ少し暗くなる)
    public static void Plate(float x, float y, float w, float h, uint[] ramp, bool outline = true, float top = 0.62f, float bottom = 0.42f)
    {
      int X0 = RI((x + Sf.OX) * S), X1 = RI((x + w + Sf.OX) * S) - 1, Y0 = RI((y + Sf.OY) * S), Y1 = RI((y + h + Sf.OY) * S) - 1;
      int n = ramp.Length;
      for (int py = Y0; py <= Y1; py++)
      {
        float k = Y1 > Y0 ? (float)(py - Y0) / (Y1 - Y0) : 0;
        for (int px = X0; px <= X1; px++)
        {
          uint c;
          bool edgeO = outline && (px == X0 || px == X1 || py == Y0 || py == Y1);
          if (edgeO)
            c = ramp[0];
          else if (py == Y0 + (outline ? 1 : 0) || px == X0 + (outline ? 1 : 0))
            c = ramp[n - 1];
          else if (py == Y1 - (outline ? 1 : 0) || px == X1 - (outline ? 1 : 0))
            c = ramp[Math.Min(1, n - 1)];
          else
            c = Pick(ramp, top + (bottom - top) * k, px, py);
          SpanPx(px, px, py, c, 255);
        }
      }
    }

    // 斜め上から見た箱 (ESP32 版と同じ形。x・gy は床の位置、hh は高さ)
    public static void block(float fx, float fgy, int w, int d, float fhh, uint top, uint front)
    {
      int x = lroundf(fx), gy = lroundf(fgy), hh = lroundf(fhh);
      for (int r = 0; r <= hh; r++)
        R(x + w, gy - hh + d + r, lroundf(r * 0.5f) + 1, 1, 0, 82);
      R(x + 2, gy + d, w + lroundf(hh * 0.5f) - 1, 2, 0, 82);
      R(x, gy - hh, w, d, top);
      R(x, gy - hh, w, 1, 0xffffff, 46);
      Rect(x, gy - hh, w, PX, 0xffffff, 40);
      R(x, gy - hh, 1, d, 0xffffff, 26);
      if (hh > 0)
      {
        R(x, gy - hh + d, w, hh, front);
        R(x, gy - hh + d, w, 1, 0, 77);
        Rect(x, gy + d - PX, w, PX, 0, 60);
      }
      R(x + w - 1, gy - hh, 1, d + hh, 0, 64);
    }

    // ディザで半分だけ塗る (網かけ)。k は塗る割合 (0〜1)
    public static void Dither(float x, float y, float w, float h, uint c, float k)
    {
      int X0 = RI((x + Sf.OX) * S), X1 = RI((x + w + Sf.OX) * S) - 1, Y0 = RI((y + Sf.OY) * S), Y1 = RI((y + h + Sf.OY) * S) - 1;
      for (int py = Y0; py <= Y1; py++)
        for (int px = X0; px <= X1; px++)
          if (Bayer(px, py) < k)
            SpanPx(px, px, py, c, 255);
    }

    // 光る玉 (外ほど薄くなる光の輪)
    public static void Glow(float cx, float cy, float r, uint c, int a)
    {
      for (int k = 3; k >= 1; k--)
        Disc(cx, cy, r * k / 3, c, a / 3);
    }
  }
}
