// 背景。下から順に 8 つの区画を積む。動かない部分は起動時に一度だけ描いておき (下の絵と、戦車より手前に出す絵の 2 枚)、
// 毎フレームはそれを写して、動くもの (anim) だけを描き足す。描き方は ESP32 版 (g2bg.cpp) と同じで、草・ひび・つた・光の筋などは 3 倍の細かさで描く
using System;
using System.Collections.Generic;

namespace Foundry
{
  public class Zone
  {
    public string Name;
    public int H;
    public Action<int> Stat;      // 動かない部分 (下の絵)
    public Action<int, int> Anim; // 毎フレーム描き足すもの
    public Action<int> Fore;      // 戦車より手前の絵 (頭上の配管や梁)。なければ null
    public Zone(string name, int h, Action<int> stat, Action<int, int> anim, Action<int> fore)
    {
      Name = name;
      H = h;
      Stat = stat;
      Anim = anim;
      Fore = fore;
    }
  }

  public class Bg : D
  {
    public static Zone[] ZONES;
    public static int NZ;
    public static int[] STARTS; // 区画の下端 (下から数えた位置)
    public static int TOT;      // 全部の高さ
    // 絵の行 r (点の行) は、いちばん上の区画の上端から数えた行
    public static int[] bgBuf, foreBuf;
    public static bool[] foreRow; // 手前の絵がある点の行
    public static bool hideCrane; // 沈殿槽の天井クレーンを描かない (ボスのガントリーが出ているあいだ)

    // ---- 色 ----
    static readonly uint[] CONC = { 0x47484a, 0x4f5052, 0x57585a, 0x5f6062 };
    static readonly uint[] DARK = { 0x1f2123, 0x242628, 0x2a2c2e, 0x303234 };
    static readonly uint[] OUTP = { 0x46473f, 0x4e4f45, 0x56574b, 0x5e5e51 };
    static readonly uint[] WARM = { 0x433f34, 0x4a4639, 0x524d3e, 0x5a5443 };
    static readonly uint[] WATER = { 0x0c1d1a, 0x0f2420, 0x132a25, 0x17302a };
    const uint YEL = 0xc9a227, YELD = 0x7a6420, LIGHT = 0xffecc4;
    const int SILVER = 0, RUST = 1, GREEN = 2, RED = 3, YELLOW = 4, LAG = 5, BLACK = 6, BLUE = 7, NPP = 8;
    static readonly uint[][] PP = {
      new uint[] { 0x33363a, 0x7d8388, 0xc4c9cc, 0xeef1f2 }, new uint[] { 0x2e170d, 0x6b3a1e, 0xa8683a, 0xd09a6a }, new uint[] { 0x1f3330, 0x3f6f6a, 0x6fa39b, 0xb8d8d0 },
      new uint[] { 0x3a1410, 0x7a2e22, 0xb4614c, 0xe8a898 }, new uint[] { 0x3a3010, 0x8a7020, 0xc9a227, 0xf0dc90 }, new uint[] { 0x5a5650, 0xa8a498, 0xd4d0c4, 0xf2efe6 },
      new uint[] { 0x0e0f10, 0x2a2c2e, 0x4f5357, 0x8a8f94 }, new uint[] { 0x173455, 0x2f5f96, 0x6a9ed2, 0xcfe3f5 },
    };

    // ---- 部品 ----
    // 床のむら: なめらかなノイズと細かい雑音を混ぜて、色を段に分ける (1 点ごと)
    static void tex(float x, float y, int w, int h, uint[] pal, int n, float sc, float seed)
    {
      for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx++)
        {
          float X = x + xx, Y = y + yy;
          float v = vn(X * sc + seed * 7.3f, Y * sc + seed * 3.1f) * 0.6f + h2(X + seed * 31, Y) * 0.25f + h2(floorf(X / 4) + seed * 17, floorf(Y / 4)) * 0.15f;
          int i = clampi((int)Math.Floor((v - 0.18f) / 0.64f * n), 0, n - 1);
          R(X, Y, 1, 1, pal[i]);
        }
      // 点の細かさのざらつき (明るい点・暗い点を少し)
      int cnt = w * h / 6;
      for (int i = 0; i < cnt; i++)
      {
        float px = x + hs(seed * 13 + i * 0.37f) * w, py = y + hs(seed * 7 + i * 0.71f) * h;
        Q(px, py, i % 2 == 0 ? Mul(pal[n - 1], 1.12f) : Mul(pal[0], 0.85f));
      }
    }
    static void specks(float x, float y, float w, float h, int n, float seed, uint c0, uint c1)
    {
      for (int i = 0; i < n; i++)
      {
        float px = x + hs(seed + i * 1.1f) * w, py = y + hs(seed + i * 2.3f) * h;
        Rect(px, py, 2 * PX, 2 * PX, i % 2 == 1 ? c1 : c0);
        if (i % 3 == 0)
          Q(px + PX, py + 2 * PX, Mul(i % 2 == 1 ? c1 : c0, 0.7f));
      }
    }
    static void cracks(float x, float y, float w, float h, int n, float seed, uint c)
    {
      for (int i = 0; i < n; i++)
      {
        float px = x + hs(seed + i * 1.7f) * w, py = y + hs(seed + i * 2.9f) * h, a = hs(seed + i * 4.1f) * 6.28f;
        for (int k = 0; k < 8; k++)
        {
          a += (hs(seed + i * 13 + k) - 0.5f) * 1.4f;
          float l = 3 + hs(seed + i * 7 + k) * 5, nx = px + cosf(a) * l, ny = py + sinf(a) * l;
          Line1(px + 0.5f, py + 0.5f, nx + 0.5f, ny + 0.5f, c);
          Line1(px + 0.5f + PX, py + 0.5f + PX, nx + 0.5f + PX, ny + 0.5f + PX, Mul(c, 0.8f), 150);
          if (hs(seed + i * 5 + k) > 0.7f) // 枝分かれ
          {
            float b = a + (hs(seed + i + k * 3) > 0.5f ? 0.9f : -0.9f);
            Line1(px + 0.5f, py + 0.5f, px + 0.5f + cosf(b) * l * 0.5f, py + 0.5f + sinf(b) * l * 0.5f, c, 180);
          }
          px = nx;
          py = ny;
        }
      }
    }
    static void drips(float x, float y, float w, float maxl, int n, float seed)
    {
      for (int i = 0; i < n; i++)
      {
        float xx = x + hs(seed + i * 1.9f) * w, l = 2 + hs(seed + i * 3.3f) * maxl;
        Rect(xx, y, 2 * PX, l, 0x6b3a1e);
        Rect(xx, y + l - PX, 2 * PX, 2 * PX, 0x4a2a1a);
        Rect(xx, y, 2 * PX, PX * 2, 0xa8683a);
        Q(xx + PX, y + l * 0.3f, 0x8a4f28);
        if (hs(seed + i) > 0.6f)
          Rect(xx + 2 * PX, y + PX, PX, l * 0.6f, 0x4a2a1a);
      }
    }
    static readonly uint[] TG = { 0x4f5f32, 0x6b7d3e, 0x8a9a52 }, TD = { 0x7d6c40, 0x9f8b55, 0xc2ad74 };
    static void tuft(float x, float y, float seed, int kind) // 0 枯れ草 1 緑 2 ススキ
    {
      int n = 5 + (int)(hs(seed) * 5);
      uint[] cols = kind == 1 ? TG : TD;
      Ell(x + 1, y + 1, 2.5f, 1.2f, 0, 70);
      for (int k = 0; k < n; k++)
      {
        float a = -PIf / 2 + (hs(seed + k * 1.3f) - 0.5f) * 1.7f;
        float l = (kind == 2 ? 9 : 4) + hs(seed + k * 2.1f) * (kind == 2 ? 7 : 4);
        float bend = (hs(seed + k * 3.7f) - 0.5f) * 0.6f;
        float mx = x + 0.5f + cosf(a) * l * 0.55f, my = y + 0.5f + sinf(a) * l * 0.55f * 0.85f;
        float ex = x + 0.5f + cosf(a + bend) * l, ey = y + 0.5f + sinf(a + bend) * l * 0.85f;
        uint c = cols[k % 3];
        Seg(x + 0.5f, y + 0.5f, mx, my, 0.6f, Mul(c, 0.85f));
        Line1(mx, my, ex, ey, c);
        if (kind == 2)
        {
          Disc(ex, ey, 0.75f, 0xeee4cc);
          Q(ex - PX, ey - PX, 0xf6efdc);
          Q(ex + PX, ey + PX, 0xcfc3a4);
          Q(ex + 0.4f, ey + 0.8f, 0xd8ccb0);
        }
      }
    }
    static void vine(float x, float y, float len, float seed, int dir)
    {
      for (int i = 0; i < len; i++)
      {
        x += (hs(seed + i * 0.7f) - 0.5f) * 1.6f;
        y += dir;
        Rect(x + PX, y, 2 * PX, 1 + PX, 0x3b4a26);
        if (i % 3 == 0) // 葉
        {
          float s = i % 6 != 0 ? 1 : -1;
          Ell(x + 0.6f + s * 0.9f, y + 0.5f, 0.9f, 0.6f, 0x6b7d3e);
          Q(x + 0.6f + s * 0.9f - PX, y + 0.2f, 0x8a9a52);
        }
        if (i % 7 == 0)
          Disc(x + 0.5f + (i % 2 == 1 ? 2 : -2), y, 0.6f, 0x8a9a52);
      }
    }
    static void railH(float x, float y, float w)
    {
      R(x + 2, y + 2, w, 1, 0, 89);
      for (float px = x; px <= x + w; px += 10)
      {
        R(px, y - 8, 1, 9, YELD);
        Rect(px, y - 8, PX, 9, 0xb08a2a);
      }
      R(x, y - 8, w, 1, YEL);
      Rect(x, y - 8, w, PX, 0xf0d470);
      R(x, y - 4, w, 1, YELD);
    }
    static void railV(float x, float y, float h)
    {
      R(x + 3, y, 1, h, 0, 89);
      for (float py = y; py < y + h; py += 10)
      {
        R(x + 1, py - 6, 2, 6, YELD);
        P(x + 1, py, 0x3a3018);
      }
      R(x, y - 8, 1, h, YEL);
      R(x - 1, y - 8, 1, h, 0xe2c25a);
      Rect(x - 1, y - 8, PX, h, 0xf6e6a0);
    }
    // 光の筋 (点の行ごとに少しずつずらす)
    static void shaft(float x0, float y0, int w, int h, float slope, float a, int gx, int gy)
    {
      int al = A(a);
      for (int pr = 0; pr < h * S; pr++)
      {
        float r = (float)pr / S;
        if (gy != 0 && (r % gy) < 2)
          continue;
        float xs = x0 + r * slope;
        if (gx != 0)
        {
          for (int c = 0; c < w; c += gx)
            R(xs + c, y0 + r, min(gx - 2, w - c), PX, LIGHT, al);
        }
        else
          R(xs, y0 + r, w, PX, LIGHT, al);
      }
    }
    static void grate(float x, float y, float w, float h)
    {
      R(x, y, w, h, 0x121314);
      for (int yy = 0; yy < h; yy += 3)
      {
        R(x, y + yy, w, 1, 0x3e4144);
        Rect(x, y + yy, w, PX, 0x585c60);
      }
      for (int xx = 0; xx < w; xx += 6)
        R(x + xx, y, 1, h, 0x2c2f32);
    }
    static void tube(List<Pt> segs, float r, uint[] pal, int seam, int shadowA)
    {
      List<Pt> pts = pathPts(segs);
      if (shadowA != 0)
        foreach (Pt p in pts)
          C(p.x + 5, p.y + 6, r, 0, shadowA);
      foreach (Pt p in pts)
        C(p.x, p.y, r, pal[0]);
      foreach (Pt p in pts)
        C(p.x - 1, p.y - 1, r - 2, pal[1]);
      foreach (Pt p in pts)
        C(p.x - r * 0.35f, p.y - r * 0.35f, max(1.0f, r * 0.4f), pal[2]);
      foreach (Pt p in pts)
        C(p.x - r * 0.45f, p.y - r * 0.45f, max(0.0f, r * 0.13f), pal[3]);
      if (seam != 0)
        for (int i = 1; i + 1 < pts.Count; i += seam)
        {
          float dx = pts[i + 1].x - pts[i - 1].x, dy = pts[i + 1].y - pts[i - 1].y, l = hypotf(dx, dy);
          if (l == 0)
            l = 1;
          float nx = -dy / l, ny = dx / l;
          L(pts[i].x - nx * (r - 1), pts[i].y - ny * (r - 1), pts[i].x + nx * (r - 1), pts[i].y + ny * (r - 1), pal[0]);
          Line1(pts[i].x + 0.5f + dx / l * 1.2f - nx * (r - 1.5f), pts[i].y + 0.5f + dy / l * 1.2f - ny * (r - 1.5f), pts[i].x + 0.5f + dx / l * 1.2f + nx * (r - 1.5f), pts[i].y + 0.5f + dy / l * 1.2f + ny * (r - 1.5f), pal[2]);
        }
    }
    static void gallery(float x0, float y0, float x1, float y1, float wd, float seed)
    {
      float dx = x1 - x0, dy = y1 - y0, l = hypotf(dx, dy), ux = dx / l, uy = dy / l, nx = -uy, ny = ux, hw = wd / 2;
      Pt[] q = { new Pt(x0 + nx * hw + 7, y0 + ny * hw + 9), new Pt(x1 + nx * hw + 7, y1 + ny * hw + 9), new Pt(x1 - nx * hw + 7, y1 - ny * hw + 9), new Pt(x0 - nx * hw + 7, y0 - ny * hw + 9) };
      poly(q, 0, 97);
      float ox = nx * (hw + 4), oy = ny * (hw + 4);
      LT(x0 + ox, y0 + oy, x1 + ox, y1 + oy, 2, 0x4a2a1a);
      for (float s = 0; s < l - 8; s += 10)
      {
        L(x0 + ux * s + ox, y0 + uy * s + oy, x0 + ux * (s + 5) + nx * hw, y0 + uy * (s + 5) + ny * hw, 0x6b3a1e);
        L(x0 + ux * (s + 5) + nx * hw, y0 + uy * (s + 5) + ny * hw, x0 + ux * (s + 10) + ox, y0 + uy * (s + 10) + oy, 0x6b3a1e);
      }
      for (float s = 0; s < l; s += 0.34f)
      {
        float px = x0 + ux * s, py = y0 + uy * s;
        int corr = ((int)(s / 3)) % 2;
        float rust = vn(s * 0.05f, seed) * 0.7f + vn(s * 0.2f, seed + 9) * 0.3f;
        for (float k = -hw; k <= hw; k += 0.34f)
        {
          float f = (k + hw) / wd;
          bool rr = rust + (hs(lroundf(s) * 3 + lroundf(k) + seed) - 0.5f) * 0.15f + (h2(s * 3, k * 3 + seed) - 0.5f) * 0.1f > 0.55f;
          uint c;
          if (rr)
            c = f < 0.3f ? 0xa8683a : f < 0.7f ? (corr != 0 ? 0x8a4f28u : 0x7a4422u) : 0x4a2a1au;
          else
            c = f < 0.3f ? 0xb8ab98 : f < 0.7f ? (corr != 0 ? 0x9c8f80u : 0x8a7e70u) : 0x5a5248u;
          Rect(px + nx * k + 0.5f, py + ny * k + 0.5f, 0.5f, 0.5f, c);
        }
      }
      LT(x0 - nx * hw, y0 - ny * hw, x1 - nx * hw, y1 - ny * hw, 1, 0xd0c4b0);
      for (int i = 0; i < 5; i++)
      {
        float s = hs(seed + i) * l;
        vine(x0 + ux * s - nx * hw, y0 + uy * s - ny * hw, 16 + hs(seed + i * 3) * 14, seed + i * 11, 1);
      }
    }
    static void ledge(float seed)
    {
      R(0, 0, W, 3, 0x76777a);
      Rect(0, 0, W, PX, 0x9a9b9e);
      R(0, 3, W, 8, 0x4a4b4d);
      R(0, 11, W, 2, 0, 89);
      drips(0, 3, W, 7, 34, seed);
    }
    static void fadedHaz(int x, int y, int w, int h)
    {
      for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx += 2)
        {
          if (yy > 3 && yy < h - 4 && xx > 3 && xx < w - 5)
            continue;
          if (hs((x + xx) * 3 + (y + yy)) < 0.25f)
            continue;
          R(x + xx, y + yy, 2, 1, (((x + xx + y + yy) >> 3) & 1) != 0 ? 0x8a7428u : 0x2a2926u);
        }
    }
    static void tree(float x, float y, float len, float a, int depth, float seed)
    {
      float ex = x + cosf(a) * len, ey = y + sinf(a) * len;
      Seg(x + 0.5f, y + 0.5f, ex + 0.5f, ey + 0.5f, depth > 2 ? 1.0f : 0.6f, depth > 2 ? 0x4a4038u : 0x6a5e52u);
      if (depth <= 0)
        return;
      tree(ex, ey, len * 0.7f, a - 0.5f - hs(seed) * 0.4f, depth - 1, seed + 1.3f);
      tree(ex, ey, len * 0.7f, a + 0.4f + hs(seed + 2) * 0.4f, depth - 1, seed + 2.7f);
    }

    // ---- 配管 ----
    static void valve(float x, float y, uint c)
    {
      CR(x, y, 4, c);
      L(x - 3, y, x + 3, y, c);
      L(x, y - 3, x, y + 3, c);
      C(x, y, 1, 0xd8d4c8);
      Ring(x + 0.5f, y + 0.5f, 4.2f, PX, Mix(c, 0xffffff, 0.4f));
    }
    static void gauge(float x, float y, float a)
    {
      C(x, y, 3, 0xe0dccf);
      CR(x, y, 3, 0x1a1a1a);
      for (int k = 0; k < 5; k++) // 目盛り
      {
        float b = -PIf * 0.9f + k * 0.45f;
        Q(x + 0.5f + cosf(b) * 2.6f, y + 0.5f + sinf(b) * 2.6f, 0x3a3a3a);
      }
      Seg(x + 0.5f, y + 0.5f, x + 0.5f + cosf(a) * 2.6f, y + 0.5f + sinf(a) * 2.6f, 0.5f, 0xa02a1a);
      Q(x + 0.4f, y + 0.4f, 0x1a1a1a);
    }
    // fl: フランジの間隔 (点の数)、sx/sy: 影のずらし (0 なら影なし)、valveAt: バルブの位置 (0〜1。0 ならなし)
    static void pipe(List<Pt> segs, float r, uint[] pal, int fl = 0, float sx = 0, float sy = 0, bool cap = false, float valveAt = 0, uint vc = 0xb0302a)
    {
      List<Pt> pts = pathPts(segs);
      if (sx != 0 || sy != 0)
      {
        BeginMask(); // 影は重ねても濃くならないように、まとめて 1 度だけ塗る
        foreach (Pt p in pts)
          C(p.x + sx, p.y + sy, r, 0, 255);
        EndMask(0, 60);
      }
      tube(segs, r, pal, 0, 0);
      if (fl != 0)
        for (int i = fl >> 1; i + 1 < pts.Count; i += fl)
        {
          float dx = pts[i + 1].x - pts[i - 1].x, dy = pts[i + 1].y - pts[i - 1].y, l = hypotf(dx, dy);
          if (l == 0)
            l = 1;
          float nx = -dy / l, ny = dx / l, tx = dx / l, ty = dy / l, x = pts[i].x, y = pts[i].y;
          L(x - nx * (r + 1), y - ny * (r + 1), x + nx * (r + 1), y + ny * (r + 1), pal[1]);
          L(x - nx * (r + 1) + tx, y - ny * (r + 1) + ty, x + nx * (r + 1) + tx, y + ny * (r + 1) + ty, pal[0]);
          P(x - nx * (r + 1), y - ny * (r + 1), pal[3]);
          // ボルト
          for (int b = -1; b <= 1; b += 2)
            Q(x + 0.5f + nx * (r + 0.4f) * b + tx * 0.3f, y + 0.5f + ny * (r + 0.4f) * b + ty * 0.3f, pal[3]);
        }
      if (cap)
      {
        Pt s = pts[0], e = pts[pts.Count - 1];
        C(s.x, s.y, r + 1, pal[1]);
        C(e.x, e.y, r + 1, pal[1]);
        C(e.x - 1, e.y - 1, max(1.0f, r - 1), pal[2]);
        Ring(e.x + 0.5f, e.y + 0.5f, r + 1.2f, PX * 2, pal[0]);
      }
      if (valveAt > 0)
      {
        Pt v = pts[(int)(pts.Count * valveAt)];
        valve(v.x - r - 2, v.y - r - 2, vc);
      }
    }
    // list は (半径, 種類) の組を並べたもの
    static int bundleW(int[] list)
    {
      int w = 0;
      for (int i = 0; i < list.Length; i += 2)
        w += 2 * list[i] + 2;
      return w;
    }
    static void bundleV(float x, float y0, float y1, int[] list, float sx, float sy)
    {
      float xx = x;
      for (int i = 0; i < list.Length; i += 2)
      {
        int r = list[i];
        pipe(Pts(xx + r, y0, xx + r, y1), r, PP[list[i + 1]], 18 + r * 3 + (i / 2) * 5, sx, sy);
        xx += 2 * r + 2;
      }
      for (float y = y0 + 20; y < y1; y += 44)
      {
        R(x - 1, y, xx - x + 1, 3, 0x1e1f20);
        R(x - 1, y, xx - x + 1, 1, 0x55585b);
        P(x, y + 1, 0x8a8f94);
        P(xx - 2, y + 1, 0x8a8f94);
      }
    }
    static void bundleH(float y, float x0, float x1, int[] list, float sx, float sy)
    {
      float yy = y;
      for (int i = 0; i < list.Length; i += 2)
      {
        int r = list[i];
        pipe(Pts(x0, yy + r, x1, yy + r), r, PP[list[i + 1]], 18 + r * 3 + (i / 2) * 5, sx, sy);
        yy += 2 * r + 2;
      }
      for (float x = x0 + 16; x < x1; x += 52)
      {
        R(x, y - 1, 3, yy - y + 1, 0x1e1f20);
        R(x, y - 1, 1, yy - y + 1, 0x55585b);
      }
    }
    static readonly int[] RS = { 2, 2, 2, 3, 3, 4, 5 };
    static void maze(float x, float y, float w, float h, int n, float seed, float sx, float sy)
    {
      for (int i = 0; i < n; i++)
      {
        float px = x + hs(seed + i) * w, py = y + hs(seed + i * 1.3f) * h;
        int dir = hs(seed + i * 2) < 0.5f ? 0 : 1;
        List<Pt> segs = new List<Pt>();
        segs.Add(new Pt(px, py));
        int nseg = 2 + (int)(hs(seed + i * 3.1f) * 3);
        for (int k = 0; k < nseg; k++)
        {
          float len = 16 + hs(seed + i * 7 + k) * 60, sg = hs(seed + i * 5 + k) < 0.5f ? -1 : 1;
          if (dir == 0)
            px = constrain(px + len * sg, x, x + w);
          else
            py = constrain(py + len * sg, y, y + h);
          segs.Add(new Pt(px, py));
          dir ^= 1;
        }
        int r = RS[(int)(hs(seed + i * 9) * 7)];
        uint[] pal = PP[(int)(hs(seed + i * 11) * NPP)];
        pipe(segs, r, pal, 14 + r * 2, sx, sy, true, hs(seed + i * 13) < 0.35f ? 0.5f : 0);
      }
    }
    static void steam(float x, float y, int t, float s = 1)
    {
      for (int k = 0; k < 5; k++)
      {
        float p = fmod1(t * 0.015f + k / 5.0f);
        float cx = x + 0.5f + sinf(p * 6 + k) * 3 * s, cy = y + 0.5f - p * 40 * s, r = (2 + p * 7) * s;
        Disc(cx, cy, r, 0xc8cdd2, A(0.18f * (1 - p)));
        Disc(cx - r * 0.2f, cy - r * 0.2f, r * 0.65f, 0xe0e4e8, A(0.16f * (1 - p)));
      }
    }

    // ---- 床の番号・梁・電灯 ----
    static string glyph(char ch)
    {
      switch (ch)
      {
        case 'A': return "010101111101101";
        case 'B': return "110101110101110";
        case 'C': return "011100100100011";
        case '-': return "000000111000000";
        case '0': return "111101101101111";
        case '1': return "010110010010111";
        case '2': return "111001111100111";
        case '6': return "111100111101111";
      }
      return null;
    }
    // 床にかすれた塗料で書いた番号
    static void mark(float x, float y, string str, int s, uint c, int a)
    {
      float xx = x;
      foreach (char ch in str)
      {
        string f = glyph(ch);
        if (f != null)
          for (int i = 0; i < 15; i++)
            if (f[i] == '1')
            {
              float bx = xx + (i % 3) * s, by = y + (i / 3) * s;
              for (int py = 0; py < s * S; py++) // かすれ: 塗りの剥げた点を抜く
                for (int px = 0; px < s * S; px++)
                {
                  float wx = bx + (float)px / S, wy = by + (float)py / S;
                  if (vn(wx * 0.7f, wy * 0.7f) * 0.7f + h2(wx * 3, wy * 3) * 0.3f < 0.3f)
                    continue;
                  Q(wx, wy, c, a);
                }
            }
        xx += 4 * s;
      }
      float w = xx - x + s, hh = 5 * s;
      R(x - 2 * s, y - 2 * s, w + 2 * s, 1, c, a);
      R(x - 2 * s, y + hh + s, w + 2 * s, 1, c, a);
    }
    static void truss(float y, bool lamps)
    {
      R(14, y + 18, W, 12, 0, 71);
      R(0, y, W, 3, 0x6b3a1e);
      R(0, y, W, 1, 0xb07040);
      Rect(0, y, W, PX, 0xd09a6a);
      R(0, y + 12, W, 3, 0x4a2a1a);
      R(0, y + 12, W, 1, 0x8a4f28);
      for (int x = 0; x < W; x += 12)
      {
        LT(x, y + 3, x + 6, y + 12, 2, 0x7a4422);
        LT(x + 6, y + 12, x + 12, y + 3, 2, 0x5a3018);
        Line1(x + 0.5f, y + 3.2f, x + 6.3f, y + 12, 0xa8683a);
        P(x, y + 1, 0xd09a6a);
        Disc(x + 0.5f, y + 1.5f, 0.6f, 0x3a2010); // リベット
        Q(x + 0.3f, y + 1.2f, 0xe0b080);
      }
      for (int i = 0; i < 10; i++)
        Rect(hs(i * 3.3f + y) * W, y + 15, 2 * PX, 2 + hs(i + y) * 5, 0x6b3a1e);
      for (int i = 0; i < 3; i++)
        vine(30 + hs(i + y) * 180, y + 14, 8 + hs(i * 2 + y) * 10, y + i, 1);
      if (lamps)
        foreach (int x in new[] { 56, 184 })
        {
          L(x, y + 15, x, y + 22, 0x2a2b2c);
          C(x, y + 25, 4, 0x4a4e52);
          CR(x, y + 25, 4, 0x2a2c2e);
          C(x - 1, y + 24, 1, 0x8a8f94);
          C(x, y + 28, 3, 0xffe6aa, 89);
          P(x, y + 28, 0xfff1c8);
        }
    }
    static void lampPool(float x, float y)
    {
      for (float r = 30; r > 6; r -= 3)
        C(x, y, r, 0xffe6aa, 7);
    }

    // ================= 区画 (ローカル座標: y=0 が上=奥) =================

    // 1. 草むらの搬入口
    static void z1(int h)
    {
      tex(0, 0, W, h, OUTP, 4, 0.045f, 1);
      specks(0, 40, W, h - 40, 600, 1, 0x6d6c5e, 0x3c3d36);
      cracks(0, 50, W, h - 50, 12, 5, 0x33342e);
      for (int i = 0; i < 46; i++)
      {
        float x = hs(i * 6.1f) * W, y = 50 + hs(i * 2.9f) * (h - 50), r = 4 + hs(i * 1.7f) * 9;
        C(x, y, r, i % 3 != 0 ? 0x3d5226u : 0x4a5e2cu);
        C(x - 2, y - 2, r * 0.6f, 0x55702f);
        specks(x - r, y - r, r * 2, r * 2, 20, i, 0x6f8a3a, 0x2e3e1c);
      }
      for (int y = 46; y < h; y += 9)
      {
        if (hs(y * 0.71f) < 0.3f)
          continue;
        R(118, y, 30, 4, 0x4a3b2c);
        R(118, y, 30, 1, 0x5e4b37);
        Rect(118, y + 4 - PX, 30, PX, 0x2e241a);
      }
      foreach (int x in new[] { 124, 141 })
      {
        R(x, 42, 2, h - 42, 0x5a3420);
        R(x, 42, 1, h - 42, 0x8a5a34);
        Rect(x, 42, PX, h - 42, 0xb08050);
      }
      mark(54, 72, "A-01", 3, 0xe1dccd, 102);
      for (int y = 46; y < h; y += 9)
      {
        if (hs(y * 0.37f) < 0.12f)
          continue;
        R(168, y, 32, 4, 0x4a3b2c);
        R(168, y, 32, 1, 0x5e4b37);
        Rect(168, y + 4 - PX, 32, PX, 0x2e241a);
      }
      foreach (int x in new[] { 175, 192 })
      {
        R(x, 42, 2, h - 42, 0x6b3a1e);
        R(x, 42, 1, h - 42, 0xa8683a);
        Rect(x, 42, PX, h - 42, 0xd09a6a);
      }
      C(62, 250, 9, 0x3a3f3c);
      C(67, 253, 7, 0x3a3f3c);
      R(56, 246, 8, 2, 0x8a979a);
      R(60, 252, 10, 1, 0x6f7c80);
      for (int y = 236; y < h; y += 30)
      {
        block(224, y, 4, 4, 12, 0x6b5a44, 0x4a3b2c);
        L(226, y - 6, 226, y + 24, 0x7d7a70);
      }
      for (int i = 0; i < 260; i++)
      {
        float e = hs(i * 5.1f);
        float x = e < 0.35f ? hs(i * 1.3f) * 64 : e < 0.7f ? 176 + hs(i * 1.3f) * 64 : hs(i * 1.3f) * W;
        float y = 48 + hs(i * 2.7f) * (h - 48), k = hs(i * 9.1f);
        tuft(x, y, i * 3.3f, k < 0.18f ? 2 : k < 0.75f ? 1 : 0);
      }
      gallery(-6, 418, 150, 168, 18, 3);
      for (int i = 0; i < 20; i++)
        tuft(10 + hs(i * 4.7f) * 150, 300 + hs(i * 2.2f) * 120, i * 7.7f, i % 3 != 0 ? 0 : 2);
      // 建屋の壁と入口
      R(0, 0, W, 6, 0x6a6b6d);
      tex(0, 6, W, 34, new uint[] { 0x3f4042, 0x47484a, 0x4f5052 }, 3, 0.08f, 9);
      for (int y = 10; y < 40; y += 5) // 壁の目地
        Rect(0, y, W, PX, 0x36373a);
      drips(0, 6, W, 22, 44, 2);
      foreach (int x in new[] { 12, 52, 172, 210 })
      {
        R(x, 13, 20, 14, 0x141516);
        R(x + 2, 15, 7, 5, 0x5d6f78);
        R(x + 12, 20, 5, 4, 0x43535b);
        Rect(x + 2, 15, 7, PX, 0x8aa0aa);
        R(x, 13, 20, 1, 0x6b3a1e);
        R(x + 10, 13, 1, 14, 0x3a2a1e);
      }
      R(88, 6, 64, 34, 0x3a2418);
      R(91, 8, 58, 32, 0x0d0e0f);
      R(91, 8, 58, 3, 0x1c1d1e);
      for (int x = 92; x < 148; x += 4) // 巻き上げたシャッター
        Rect(x, 8, PX, 3, 0x2a2b2c);
      R(0, 40, W, 3, 0, 102);
      for (int i = 0; i < 26; i++)
        tuft(hs(i * 3.1f) * W, 42 + hs(i) * 6, i * 5.5f, i % 4 != 0 ? 0 : 1);
      vine(40, 6, 30, 4, 1);
      vine(160, 6, 34, 8, 1);
      vine(205, 6, 26, 12, 1);
    }
    static void z1a(int h, int t)
    {
      for (int i = 0; i < 16; i++) // 舞う綿毛
      {
        float ph = fmod1(t * 0.003f + hs(i));
        float x = fmodf(hs(i + 3) * W + ph * 90, W), y = 60 + hs(i + 9) * (h - 80) - ph * 40 + sinf(t * 0.05f + i) * 3;
        Disc(x + 0.5f, y + 0.5f, 0.55f, 0xefe6d0);
        Q(x + 0.5f + PX, y + 0.5f + PX, 0xb8b0a0);
      }
    }
    static void z1f(int h)
    {
      for (int y = 70; y < h; y += 60)
      {
        R(4, y, 2, 30, 0x4a2a1a);
        R(28, y, 2, 30, 0x4a2a1a);
        L(4, y, 29, y + 30, 0x6b3a1e);
        L(29, y, 4, y + 30, 0x6b3a1e);
      }
      gallery(17, h + 10, 17, 44, 22, 11);
      truss(250, false);
    }

    // 2. 基礎の並ぶ建屋
    static void z2(int h)
    {
      tex(0, 0, W, h, WARM, 4, 0.05f, 2);
      specks(0, 0, W, h, 400, 7, 0x3a362d, 0x6a6450);
      cracks(0, 0, W, h, 10, 9, 0x36322a);
      for (int i = 0; i < 9; i++) // さびの水たまり
      {
        float x = 20 + hs(i * 4.4f) * 200, y = 40 + hs(i * 6.1f) * 380;
        C(x, y, 5 + hs(i) * 6, 0x6e5320);
        C(x + 5, y + 2, 4 + hs(i + 1) * 5, 0x7a5a22);
        R(x - 3, y - 2, 5, 1, 0xb08a3a);
      }
      for (int i = 0; i < 16; i++)
      {
        float x = hs(i * 8.8f) * W, y = hs(i * 3.9f) * h;
        C(x, y, 2 + hs(i) * 3, 0x4f5f32);
        P(x - 1, y - 1, 0x6b7d3e);
      }
      shaft(0, 24, 176, 120, 0.25f, 0.11f, 16, 12);
      shaft(40, 176, 176, 128, 0.25f, 0.11f, 16, 12);
      shaft(10, 326, 176, 100, 0.25f, 0.11f, 16, 12);
      for (int k = 0; k < 9; k++)
        for (int pr = 0; pr < h * S; pr++)
        {
          float r = (float)pr / S;
          R(k * 36 - 90 + r * 0.25f, r, 3, PX, 0, 36);
        }
      for (int y = 56; y < h - 24; y += 34)
        for (int x = 46; x < 190; x += 26)
        {
          float s = x * 7 + y;
          if (hs(s) < 0.12f)
            continue;
          int hh = (int)(8 + hs(s + 1) * 8);
          float xx = x + (hs(s + 3) - 0.5f) * 3;
          block(xx, y, 14, 10, hh, hs(s + 2) < 0.25f ? 0x6f7a4au : 0x8d8a80u, 0x5e5c55);
          // アンカーボルト
          Disc(lroundf(xx) + 2.5f, y - hh + 2.5f, 0.7f, 0x3a3830);
          Disc(lroundf(xx) + 11.5f, y - hh + 2.5f, 0.7f, 0x3a3830);
          Q(lroundf(xx) + 2.2f, y - hh + 2.2f, 0xb0aca0);
          Q(lroundf(xx) + 11.2f, y - hh + 2.2f, 0xb0aca0);
          drips(xx, y - hh + 10, 14, hh - 3, 3, s);
        }
      for (int y = 80; y < h; y += 110)
        foreach (int x in new[] { 4, 212 })
        {
          block(x, y, 24, 18, 40, 0x7d7b74, 0x55534d);
          drips(x, y - 22, 24, 30, 7, y + x);
          vine(x + 6 + hs(y) * 10, y - 22, 34, y + x, 1);
        }
      lampPool(66, 182);
      lampPool(194, 182);
      mark(150, 392, "B-02", 3, 0xe1dccd, 107);
      ledge(21);
    }
    static void z2a(int h, int t)
    {
      for (int i = 0; i < 34; i++) // 光の筋の中のほこり
      {
        float ph = fmod1(t * 0.002f + hs(i * 1.7f));
        float x = 10 + hs(i * 2.3f) * 200 + sinf(t * 0.02f + i) * 4, y = 30 + hs(i * 3.1f) * (h - 60) - ph * 30;
        Disc(x + 0.5f, y + 0.5f, 0.5f, 0xfff0d2, A(0.7f * sinf(ph * PIf)));
      }
    }
    static void z2f(int h)
    {
      truss(110, true);
      bundleH(250, 0, W, new[] { 4, RUST, 3, LAG, 2, SILVER, 2, RED }, 12, 16);
      pipe(Pts(60, 250, 60, 200, 0, 200), 3, PP[SILVER], 16, 12, 16);
    }

    // 3. ダクトと階段
    static void z3(int h)
    {
      tex(0, 0, W, h, DARK, 4, 0.06f, 3);
      specks(0, 0, W, h, 320, 3, 0x36383a, 0x1a1b1c);
      for (int y = 300; y < 470; y += 22) // さびた床板
        for (int x = 0; x < 60; x += 22)
        {
          R(x, y, 20, 20, 0x4a4440);
          R(x, y, 20, 1, 0x6a625c);
          Rect(x, y + 20 - PX, 20, PX, 0x2a2420);
          for (int k = 0; k < 4; k++)
            C(x + hs(x + y + k) * 18, y + hs(x * y + k) * 18, 1 + hs(k + y) * 2, k % 2 != 0 ? 0x8a4f28u : 0x6b3a1eu);
          foreach (float bx in new[] { 1.5f, 18.5f }) // 角のボルト
            foreach (float by in new[] { 1.5f, 18.5f })
            {
              Disc(x + bx, y + by, 0.6f, 0x2a2420);
              Q(x + bx - PX, y + by - PX, 0x8a827a);
            }
        }
      grate(56, 480, 128, 40);
      grate(56, 118, 128, 52);
      for (int y = 172; y < 478; y += 9) // 階段
      {
        R(94, y, 52, 6, 0x7f8384);
        R(94, y, 52, 1, 0xb3b7b8);
        Rect(94, y, 52, PX, 0xd8dcdd);
        for (int x = 96; x < 146; x += 3) // すべり止め
          Rect(x, y + 2, PX, 3, 0x6a6e70);
        R(94, y + 6, 52, 3, 0x3b3e40);
        if (hs(y * 0.13f) > 0.6f)
          R(96 + hs(y) * 30, y + 1, 12, 4, 0x8a5a3a);
      }
      R(90, 170, 4, 310, 0x3a3d40);
      R(146, 170, 4, 310, 0x3a3d40);
      railV(88, 172, 306);
      railV(152, 172, 306);
      railH(56, 172, 34);
      railH(150, 172, 34);
      // 奥の壁と扉と電灯
      R(0, 0, W, 6, 0x4a4b4d);
      tex(0, 6, W, 112, new uint[] { 0x2a2b2c, 0x303132, 0x363738 }, 3, 0.07f, 13);
      drips(0, 6, W, 40, 50, 6);
      for (int k = 0; k < 4; k++)
      {
        R(0, 26 + k * 6, W, 3, k % 2 != 0 ? 0x3a3d40u : 0x505458u);
        Rect(0, 26 + k * 6, W, PX, k % 2 != 0 ? 0x4a4d50u : 0x6a6e72u);
      }
      R(98, 72, 44, 46, 0x3a2a1e);
      R(101, 75, 38, 43, 0x0c0d0e);
      R(118, 75, 1, 43, 0x1c1d1e);
      for (float r = 34; r > 4; r -= 3)
        C(120, 62, r, 0xffe6aa, 7);
      R(114, 58, 12, 5, 0x6a6f73);
      R(116, 62, 8, 2, 0xfff1c8);
      shaft(92, 120, 56, 50, 0, 0.08f, 0, 0);
      block(30, 230, 34, 26, 22, 0x4a4f53, 0x353a3e);
      foreach (int x in new[] { 38, 54 })
      {
        C(x, 216, 5, 0xd8d4c8);
        CR(x, 216, 5, 0x2a2b2c);
        L(x, 216, x + 3, 213, 0xa02a1a);
      }
      // 壁の配管と脇の配管
      bundleH(10, 0, W, new[] { 2, YELLOW, 3, SILVER, 2, RED, 4, RUST }, 0, 0);
      int[] VX = { 12, 20, 44, 190, 204, 226 }, VK = { SILVER, RUST, GREEN, BLACK, YELLOW, SILVER };
      for (int i = 0; i < 6; i++)
        pipe(Pts(VX[i], 10, VX[i], 118), 2, PP[VK[i]], 20);
      gauge(30, 80, -0.8f);
      gauge(214, 80, -0.8f);
      maze(0, 140, 60, 330, 9, 31, 2, 3);
      maze(180, 140, 60, 330, 9, 37, 2, 3);
      // ダクト
      List<Pt> d1 = Pts(26, 560, 26, 262);
      arc(d1, 66, 262, 40, PIf, PIf * 1.5f);
      d1.Add(new Pt(66, 30));
      tube(d1, 15, PP[SILVER], 10, 20);
      List<Pt> d2 = Pts(214, 560, 214, 302);
      arc(d2, 174, 302, 40, 0, -PIf / 2);
      d2.Add(new Pt(174, 30));
      tube(d2, 15, PP[BLUE], 10, 20);
      foreach (int x in new[] { 66, 174 })
      {
        R(x - 18, 26, 36, 6, 0x5a5e62);
        R(x - 18, 26, 36, 1, 0x8a9094);
      }
      tube(Pts(0, 205, W, 205), 4, PP[SILVER], 0, 64);
    }
    static void z3a(int h, int t)
    {
      if (hs(t / 5) > 0.93f) // 電灯がまたたく
      {
        C(120, 62, 30, 0x0a0a0a, 89);
        R(116, 62, 8, 2, 0x8a7a5a);
      }
      for (int i = 0; i < 14; i++)
      {
        float ph = fmod1(t * 0.004f + hs(i * 2.1f));
        Disc(96 + hs(i * 4.4f) * 48 + sinf(t * 0.03f + i) * 3 + 0.5f, 70 + hs(i * 1.3f) * 90 - ph * 20 + 0.5f, 0.5f, 0xffecbe, A(0.8f * sinf(ph * PIf)));
      }
      float p = (t % 70) / 70.0f; // しずく
      if (p < 0.5f)
      {
        Disc(150.5f, 210 + p * 120, 0.6f, 0x9fb6c0);
        Q(150.3f, 209.6f + p * 120, 0xe0f0f8);
      }
      steam(40, 300, t, 0.8f);
      steam(200, 220, t + 30, 0.8f);
    }
    static void z3f(int h)
    {
      bundleH(320, 0, W, new[] { 3, RUST, 2, SILVER, 2, YELLOW }, 10, 14);
      pipe(Pts(W, 400, 196, 400, 196, 320), 3, PP[GREEN], 14, 10, 14);
    }

    // 4. 配管の迷路
    static void z8(int h)
    {
      tex(0, 0, W, h, DARK, 4, 0.06f, 23);
      specks(0, 0, W, h, 360, 23, 0x36383a, 0x1a1b1c);
      grate(70, 0, 100, h);
      R(68, 0, 2, h, 0x3a3d40);
      R(170, 0, 2, h, 0x3a3d40);
      maze(30, 16, 180, h - 32, 18, 3, 2, 3);
      int[,] cr = { { 150, RUST, 4 }, { 372, BLACK, 5 } };
      for (int i = 0; i < 2; i++)
      {
        int y = cr[i, 0], k = cr[i, 1], r = cr[i, 2];
        pipe(Pts(0, y, W, y), r, PP[k], 16, 2, 3);
        block(78, y - 10, 84, 20, 5, 0x3e4144, 0x2a2c2e);
        for (int xx = 80; xx < 160; xx += 4)
          R(xx, y - 15, 1, 20, 0x1a1b1c);
      }
      bundleV(0, 0, h, new[] { 4, LAG, 3, RUST, 5, SILVER, 2, YELLOW }, 3, 4);
      int[] right = { 2, RED, 5, GREEN, 3, SILVER, 4, BLACK };
      bundleV(W - bundleW(right), 0, h, right, 3, 4);
      foreach (Pt p in new[] { new Pt(50, 110), new Pt(190, 470) })
      {
        block(p.x - 15, p.y, 30, 22, 16, 0x4a5a52, 0x33403a);
        C(p.x, p.y - 6, 7, 0x5f7068);
        CR(p.x, p.y - 6, 7, 0x2a3430);
        C(p.x - 2, p.y - 8, 2, 0x8aa098);
        pipe(Pts(p.x + 15, p.y - 4, p.x + 30, p.y - 4, p.x + 30, p.y + 30), 3, PP[GREEN], 12, 0, 0, true);
      }
      pipe(Pts(30, 290, 210, 290), 5, PP[RUST], 14, 3, 4, true);
      for (int k = 0; k < 8; k++)
      {
        float x = 44 + k * 22;
        pipe(Pts(x, 296, x, 336), 2, PP[SILVER], 0, 0, 0, true);
        valve(x, 318, k % 3 != 0 ? 0xb0302au : 0xc9a227u);
      }
      gauge(56, 278, -0.9f);
      gauge(184, 278, -0.9f);
      railV(66, 12, h - 12);
      railV(174, 12, h - 12);
      ledge(77);
    }
    static void z8a(int h, int t)
    {
      steam(52, 290, t);
      steam(196, 150, t + 25, 0.8f);
      steam(120, 440, t + 50, 0.7f);
      gauge(56, 278, -0.9f + sinf(t * 0.13f) * 0.35f);
      gauge(184, 278, -0.9f + sinf(t * 0.13f + 2) * 0.35f);
      float p = (t % 50) / 50.0f;
      if (p < 0.6f)
      {
        Disc(88.5f, 160 + p * 60, 0.6f, 0x9fb6c0);
        Q(88.3f, 159.6f + p * 60, 0xe0f0f8);
      }
    }
    static void z8f(int h)
    {
      bundleH(64, 0, W, new[] { 3, SILVER, 4, RUST, 2, YELLOW, 3, GREEN }, 10, 14);
      bundleH(214, 0, W, new[] { 5, LAG, 3, BLACK }, 12, 16);
      bundleH(420, 0, W, new[] { 4, BLUE, 3, RED, 2, SILVER }, 10, 14);
      pipe(Pts(150, 92, 150, 140, W, 140), 3, PP[SILVER], 16, 10, 14);
      pipe(Pts(40, 236, 40, 270, 0, 270), 2, PP[YELLOW], 12, 10, 14);
    }

    // 5. 止まったプレス
    static void press3D(float cx, float cy, float elev, float seed)
    {
      float bx = cx - 40, by = cy - 22;
      fadedHaz(lroundf(bx - 8), lroundf(by - 8), 96, 64);
      block(bx, by, 80, 44, 8, 0x5b5650, 0x3e3a36);
      R(cx - 24, by - 4, 48, 22, 0x6f747a);
      R(cx - 24, by - 4, 48, 1, 0x9aa0a5);
      for (int k = 0; k < 6; k++) // 型の溝
        Rect(cx - 20 + k * 8, by, PX * 2, 14, 0x585c62);
      foreach (int dx in new[] { -40, 32 })
        block(cx + dx, by - 4, 8, 8, 74, 0x7a6a5c, 0x56463a);
      R(bx + 4 + elev * 0.3f, by + elev * 0.4f - 2, 72, 32, 0, 89);
      float hy = by - elev;
      block(cx - 36, hy, 72, 30, 16, 0x7a6656, 0x55402f);
      for (int k = 1; k < 6; k++)
      {
        R(cx - 36 + k * 12, hy - 14, 2, 28, 0x5f4d40);
        Rect(cx - 36 + k * 12, hy - 14, PX, 28, 0x907a68);
      }
      C(cx, hy - 1, 7, 0x8b8076);
      C(cx - 1, hy - 2, 3, 0xb0a596);
      Ring(cx + 0.5f, hy - 0.5f, 7.3f, PX * 2, 0x4a3e34);
      drips(cx - 36, hy + 14, 72, 10, 12, seed);
      for (int i = 0; i < 3; i++)
        vine(cx - 30 + hs(seed + i) * 60, hy + 14, 10 + hs(seed + i * 2) * 14, seed + i * 5, 1);
      foreach (int dx in new[] { -40, 32 })
      {
        block(cx + dx, by + 36, 8, 8, 74, 0x7a6a5c, 0x56463a);
        drips(cx + dx, by - 30, 8, 30, 2, seed + dx);
      }
    }
    static void z4(int h)
    {
      tex(0, 0, W, h, new uint[] { 0x33322f, 0x3a3936, 0x413f3b, 0x484640 }, 4, 0.05f, 4);
      specks(0, 0, W, h, 320, 4, 0x2a2927, 0x5a5852);
      for (int i = 0; i < 8; i++) // 油じみ
      {
        float x = hs(i * 2.2f) * W, y = hs(i * 5.5f) * h;
        C(x, y, 4 + hs(i) * 7, 0x2a2926);
        C(x + 3, y + 2, 3 + hs(i + 2) * 4, 0x26251f);
        Ell(x - 1, y - 1, 2, 0.7f, 0x4a4a50, 120); // 油の照り
      }
      for (int i = 0; i < 6; i++)
      {
        float x = 20 + hs(i * 6.6f) * 200, y = 30 + hs(i * 1.4f) * 380;
        C(x, y, 4 + hs(i) * 4, 0x6e5320);
        R(x - 2, y - 2, 4, 1, 0xb08a3a);
      }
      for (int i = 0; i < 18; i++)
        R(hs(i * 3.3f) * W, hs(i * 7.1f) * h, 4 + hs(i) * 8, 2, i % 2 != 0 ? 0x6b3a1eu : 0x5a5852u);
      for (int y = 0; y < h; y += 12)
        R(124, y, 3, 6, 0xa08228, 115);
      press3D(68, 140, 30, 41);
      press3D(68, 360, 12, 77);
      int[] bl = { 3, RUST, 2, SILVER, 4, BLACK };
      bundleV(W - bundleW(bl), 13, h, bl, 3, 4);
      pipe(Pts(0, 250, 24, 250, 24, 300, 110, 300), 3, PP[RUST], 14, 3, 4, true, 0.6f);
      pipe(Pts(0, 40, 140, 40, 140, 70), 2, PP[YELLOW], 12, 3, 4, true);
      ledge(44);
    }
    // 区画のローカルの y0〜y1 が、いま描いている所にかかるか
    static bool rowsVisible(float y0, float y1) { return (y1 + Sf.OY) * S >= Sf.CY0 && (y0 + Sf.OY) * S <= Sf.CY1; }

    static void z4a(int h, int t)
    {
      if (!rowsVisible(252 - 140, 252 + 70)) // 動くプレス (ヘッドが上がると上へ伸びる)
        return;
      float p = (t % 150) / 150.0f, z;
      if (p < 0.7f)
        z = 34;
      else if (p < 0.73f)
        z = 34 - (p - 0.7f) / 0.03f * 32;
      else if (p < 0.8f)
        z = 2;
      else
        z = 2 + (p - 0.8f) / 0.2f * 32;
      press3D(170, 252, z, 63);
      if (p >= 0.73f && p < 0.79f)
      {
        float q = (p - 0.73f) / 0.06f;
        for (int k = 0; k < 14; k++)
        {
          float a = k / 14.0f * PIf * 2 + 0.3f, r0 = 40 + q * 18;
          float x = 170 + cosf(a) * r0 * 1.1f, y = 238 + sinf(a) * r0 * 0.6f;
          Seg(x + 0.5f, y + 0.5f, x + 0.5f + cosf(a) * 4, y + 0.5f + sinf(a) * 2, 0.6f, q < 0.5f ? 0xffe27au : 0xf7841fu);
        }
        for (int k = 0; k < 8; k++)
          C(130 + k * 11, 270 + sinf(k) * 3, 3 + q * 4, 0xa09682, A(0.25f * (1 - q)));
      }
    }

    // 6. 沈殿槽
    const int PX0 = 26, PW0 = 188, PY0 = 70, PH0 = 310;
    static void z5(int h)
    {
      tex(0, 0, W, h, CONC, 4, 0.05f, 5);
      specks(0, 0, W, h, 320, 5, 0x3a3b3d, 0x6a6b6d);
      R(PX0 - 6, PY0 - 6, PW0 + 12, 6, 0x77787a);
      tex(PX0, PY0, PW0, 50, new uint[] { 0x3a3c3b, 0x414342, 0x484a49 }, 3, 0.07f, 15);
      drips(PX0, PY0, PW0, 30, 40, 9);
      for (float xx = PX0; xx < PX0 + PW0; xx += 2 * PX) // 水ぎわのこけ
      {
        float l = 6 + vn(xx * 0.1f, 2) * 10 + h2(xx * 3, 7) * 2;
        Rect(xx, PY0 + 50 - l, 2 * PX, l, ((int)(xx * 3) % 4) != 0 ? 0x2f4a32u : 0x3a5a3au);
      }
      tex(PX0, PY0 + 50, PW0, PH0 - 50, WATER, 4, 0.035f, 6);
      R(PX0 - 6, PY0 - 6, 6, PH0 + 12, 0x6f7072);
      R(PX0 + PW0, PY0 - 6, 6, PH0 + 12, 0x6f7072);
      R(PX0, PY0 + 50, 5, PH0 - 50, 0, 89);
      R(PX0 - 6, PY0 + PH0, PW0 + 12, 6, 0x77787a);
      R(PX0 - 6, PY0 + PH0 + 6, PW0 + 12, 16, 0x4b4c4e);
      drips(PX0 - 6, PY0 + PH0 + 6, PW0 + 12, 12, 30, 19);
      R(PX0 - 6, PY0 + PH0 + 22, PW0 + 12, 3, 0, 89);
      railH(PX0 - 6, PY0 - 6, PW0 + 12);
      railV(PX0 - 4, PY0 - 2, PH0);
      foreach (Pt b in new[] { new Pt(8, 290), new Pt(10, 312) }) // ドラム缶
      {
        R(b.x, b.y, 12, 10, 0x24548a);
        R(b.x, b.y - 6, 12, 7, 0x2f63a0);
        R(b.x + 1, b.y - 5, 4, 1, 0x6a9ed2);
        Rect(b.x, b.y + 3, 12, PX, 0x1a3a60);
        C(b.x + 6, b.y - 3, 1, 0x173455);
      }
      for (int i = 0; i < 10; i++)
        tuft(hs(i * 2.8f) * W, PY0 + PH0 + 30 + hs(i) * 30, i * 4.1f, 1);
      pipe(Pts(0, 50, W, 50), 3, PP[RUST], 18, 3, 4);
      pipe(Pts(0, 40, W, 40), 2, PP[SILVER], 22, 3, 4);
      pipe(Pts(0, 150, 18, 150, 18, 120, 44, 120, 44, 210), 3, PP[SILVER], 14, 3, 4, false, 0.3f);
      pipe(Pts(228, 30, 228, 400), 3, PP[GREEN], 18, 3, 4);
      pipe(Pts(W, 300, 200, 300, 200, 330), 2, PP[RED], 12, 0, 0, true);
      ledge(55);
    }
    static void z5a(int h, int t)
    {
      for (int i = 0; i < 18; i++) // 水面のさざ波
      {
        float p2 = fmod1(t * 0.01f + hs(i)), rx = PX0 + 10 + hs(i * 3.3f) * (PW0 - 30), ry = PY0 + 60 + hs(i * 1.7f) * (PH0 - 70);
        Rect(rx, ry, 4 + p2 * 10, PX * 2, 0x5a8c78, A(0.35f * (1 - p2)));
      }
      for (int i = 0; i < 46; i++) // 浮かぶごみ
      {
        float dir = hs(i * 0.7f) > 0.5f ? 1 : -1;
        float dx = fmodf(fmodf(hs(i * 2) * PW0 + t * 0.06f * dir, PW0) + PW0, PW0);
        float dy = PY0 + 56 + hs(i * 5) * (PH0 - 64) + sinf(t * 0.02f + i) * 2;
        Rect(PX0 + dx, dy, 2 * PX, 2 * PX, i % 3 != 0 ? 0xc9bf9au : 0x9e9472u);
        if (i % 4 == 0)
          Rect(PX0 + dx + 2 * PX, dy, 2 * PX, PX, 0xb3a983);
      }
      float q = (t % 60) / 60.0f;
      Ring(44.5f, 214.5f, 4 + q * 8, PX * 2, 0x78aa96, A(0.5f * (1 - q)));
      if (hideCrane)
        return;
      float cy = 50 + (h - 110) * (0.5f + 0.5f * sinf(t * 0.004f)), tx = 120 + 70 * sinf(t * 0.007f);
      R(0, cy + 24, W, 11, 0, 71);
      R(tx + 2, cy + 22, 18, 14, 0, 71);
      R(0, cy, W, 11, 0x2f6b52);
      R(0, cy, W, 1, 0x6fb595);
      R(0, cy + 10, W, 1, 0x1d3f31);
      R(0, cy + 2, W, 7, 0x24473a);
      for (int xx = 0; xx < W; xx += 8)
      {
        L(xx, cy + 2, xx + 4, cy + 8, 0x4f9a78);
        L(xx + 4, cy + 8, xx + 8, cy + 2, 0x4f9a78);
      }
      block(tx - 9, cy - 3, 18, 14, 6, 0xd0b040, 0x8a7020);
      L(tx, cy + 11, tx, cy + 17, 0x2a2b2c);
      CR(tx, cy + 19, 2, 0x3a3b3c);
    }

    // 7. 大きな建屋
    static void chair(float x, float y)
    {
      poly(new[] { new Pt(x, y + 7), new Pt(x + 8, y + 7), new Pt(x + 22, y + 15), new Pt(x + 14, y + 15) }, 0, 89);
      foreach (Pt l in new[] { new Pt(x, y + 6), new Pt(x + 7, y + 6), new Pt(x, y), new Pt(x + 7, y) })
        L(l.x, l.y, l.x, l.y + 5, 0x3a3430);
      R(x, y, 8, 7, 0x8a5a32);
      R(x, y, 8, 1, 0xa87448);
      R(x, y - 9, 1, 9, 0x5a3a22);
      R(x + 7, y - 9, 1, 9, 0x5a3a22);
      R(x, y - 9, 8, 3, 0x7a4a2a);
    }
    static void z6(int h)
    {
      tex(0, 0, W, h, new uint[] { 0x55524d, 0x5a5752, 0x625f59, 0x6a665f }, 4, 0.03f, 7);
      specks(0, 0, W, h, 480, 7, 0x4a4744, 0x76726a);
      cracks(0, 80, W, h - 80, 8, 13, 0x484540);
      for (int i = 0; i < 6; i++)
        C(hs(i * 9.2f) * W, 100 + hs(i * 1.9f) * 400, 10 + hs(i) * 12, 0x28241e, 31);
      for (int k = 0; k < 10; k++)
        for (int pr = 72 * S; pr < h * S; pr++)
        {
          float r = (float)pr / S;
          R(k * 34 - 120 + r * 0.45f, r, 4, PX, 0, 31);
        }
      foreach (int y0 in new[] { 100, 214, 330, 430 })
        shaft(-30, y0, 150, 74, 0.55f, 0.15f, 22, 18);
      C(168, 300, 10, 0x4a4844);
      C(176, 304, 8, 0x4a4844);
      R(164, 297, 14, 2, 0xa9a59a);
      for (int y = 120; y < h; y += 100)
        foreach (int x in new[] { 0, 216 })
        {
          block(x, y, 24, 16, 56, 0x8d8a83, 0x6a6760);
          drips(x, y - 40, 24, 30, 6, x + y);
          if (hs(x + y) > 0.4f)
            vine(x + 8, y - 40, 40, x + y, 1);
        }
      tex(0, 0, W, 72, new uint[] { 0x3d3b37, 0x45423d, 0x4d4a44 }, 3, 0.06f, 8);
      drips(0, 0, W, 34, 50, 8);
      R(0, 50, W, 22, 0x141210, 89);
      R(14, 30, 26, 42, 0x2a2622);
      R(16, 32, 22, 40, 0x121110);
      R(30, 50, 2, 2, 0x8a8070);
      tube(Pts(0, 12, W, 12), 4, PP[SILVER], 0, 0);
      for (int i = 0; i < 9; i++)
      {
        float x = 72 + i * 12;
        tube(Pts(x, 12, x, 60), 3, PP[SILVER], 0, 0);
        R(x - 3, 58, 6, 6, 0x3a3836);
        R(x - 1, 60, 2, 2, 0x7a3a2c);
      }
      R(62, 64, 120, 6, 0x5a4f45);
      R(62, 64, 120, 1, 0x7a6e62);
      bundleH(24, 0, W, new[] { 2, RED, 2, YELLOW }, 0, 0);
      pipe(Pts(190, 20, 190, 44, 228, 44, 228, 72), 3, PP[RUST], 12);
      pipe(Pts(50, 12, 50, 52, 20, 52), 2, PP[GREEN], 10);
      gauge(204, 34, -0.5f);
      R(0, 72, W, 5, 0, 89);
      chair(150, 104);
      lampPool(66, 372);
      lampPool(194, 372);
      mark(100, 480, "C-06", 3, 0xe1dccd, 107);
    }
    static readonly int[] BAND6 = { 100, 214, 330, 430 };
    static void z6a(int h, int t)
    {
      for (int i = 0; i < 40; i++)
      {
        float ph = fmod1(t * 0.0018f + hs(i * 1.3f));
        float r = hs(i * 4.1f) * 74, x = -30 + r * 0.55f + hs(i * 2.9f) * 140 + sinf(t * 0.015f + i) * 4, y = BAND6[i % 4] + r - ph * 24;
        Disc(x + 0.5f, y + 0.5f, 0.5f, 0xfff4dc, A(0.8f * sinf(ph * PIf)));
      }
    }
    static void z6f(int h)
    {
      truss(300, true);
      bundleH(186, 0, W, new[] { 3, SILVER, 4, RUST, 2, YELLOW }, 14, 18);
      bundleH(396, 0, W, new[] { 5, LAG, 3, BLACK, 2, RED }, 14, 18);
      pipe(Pts(180, 186, 180, 150, W, 150), 3, PP[SILVER], 14, 14, 18);
    }

    // 8. 溶鉱炉の跡
    static void z7(int h)
    {
      tex(0, 0, W, h, OUTP, 4, 0.045f, 17);
      specks(0, 0, W, h, 560, 17, 0x6d6c5e, 0x3c3d36);
      for (int i = 0; i < 12; i++)
      {
        float x = hs(i * 3.7f) * W, y = hs(i * 8.1f) * h;
        for (int k = 0; k < 6; k++)
          C(x + (hs(i + k) - 0.5f) * 14, y + (hs(i * k + 1) - 0.5f) * 10, 3 + hs(k + i) * 3, k % 2 != 0 ? 0x3a4226u : 0x4a4530u);
      }
      C(44, 96, 22, 0, 89);
      C(40, 90, 20, 0x6a6d70);
      CR(40, 90, 20, 0x3a3d40);
      C(38, 88, 14, 0x7d8184);
      C(36, 86, 6, 0xa3a8ac);
      C(40, 90, 3, 0x2a2b2c);
      for (int k = 0; k < 12; k++) // タンクのふたのボルト
      {
        float a = k / 12f * PIf * 2;
        Disc(40.5f + cosf(a) * 17.5f, 90.5f + sinf(a) * 17.5f, 0.6f, 0x3a3d40);
      }
      drips(22, 96, 36, 12, 10, 3);
      const float tx = 150, ty = 120;
      LT(tx, ty + 100, tx + 74, ty + 100, 2, 0x4a2a1a);
      LT(tx, ty + 30, tx, ty + 100, 2, 0x4a2a1a);
      LT(tx + 74, ty + 30, tx + 74, ty + 100, 2, 0x4a2a1a);
      L(tx, ty + 100, tx + 74, ty + 30, 0x6b3a1e);
      L(tx + 74, ty + 100, tx, ty + 30, 0x6b3a1e);
      poly(new[] { new Pt(tx + 6, ty + 30), new Pt(tx + 82, ty + 30), new Pt(tx + 94, ty + 76), new Pt(tx + 18, ty + 76) }, 0, 77);
      grate(tx - 4, ty - 6, 82, 50);
      foreach (int dx in new[] { 0, 74 })
        R(tx + dx - 1, ty + 44, 2, 56, 0x8a4f28);
      railH(tx - 4, ty - 4, 82);
      railV(tx - 4, ty, 40);
      uint[] HOP = { 0x5a3a22, 0x4a2a1a, 0x3a1e12, 0x20120a };
      for (int k = 0; k < 4; k++)
        R(tx + 6 + k * 4, ty + 6 + k * 4, 30 - k * 8, 30 - k * 8, HOP[k]);
      poly(new[] { new Pt(tx + 40, ty - 50), new Pt(tx + 80, ty - 50), new Pt(tx + 90, ty - 14), new Pt(tx + 50, ty - 14) }, 0, 77);
      R(tx + 34, ty - 30, 42, 22, 0x6a5a4c);
      drips(tx + 34, ty - 30, 42, 14, 10, 31);
      T(tx + 30, ty - 30, tx + 80, ty - 30, tx + 55, ty - 66, 0x8a5a3a);
      T(tx + 30, ty - 30, tx + 55, ty - 66, tx + 30, ty - 62, 0xa8683a);
      R(tx + 30, ty - 63, 26, 1, 0xc98a52);
      for (int i = 0; i < 4; i++)
        vine(tx + 6 + i * 20, ty - 6, 30 + hs(i) * 20, 50 + i, 1);
      gallery(tx + 60, ty + 30, tx + 150, ty + 260, 16, 8);
      const float fx = 74, fy = 340;
      C(fx + 6, fy + 8, 60, 0, 97);
      for (int k = 0; k < 9; k++)
      {
        float a = k / 9.0f * PIf * 2 + 0.3f;
        LT(fx + cosf(a) * 52, fy + sinf(a) * 52, fx + cosf(a) * 68, fy + sinf(a) * 68, 3, 0x5a3422);
        C(fx + cosf(a) * 68, fy + sinf(a) * 68, 3, 0x6b3a1e);
      }
      C(fx, fy, 56, 0x5a3422);
      CR(fx, fy, 56, 0x8a4f28);
      CR(fx, fy, 53, 0x3a2216);
      for (float a = 0; a < 6.28f; a += 0.02f) // 炉の縁のさび
      {
        float v = vn(a * 3, 4);
        if (v > 0.5f)
          for (float r = 44; r < 54; r += 0.5f)
            if (hs(a * 100 + r * 2) > 0.55f)
              Rect(fx + 0.5f + cosf(a) * r, fy + 0.5f + sinf(a) * r, 0.5f, 0.5f, v > 0.65f ? 0xa8683au : 0x7a4422u);
      }
      C(fx, fy, 44, 0x3d2a22);
      CR(fx, fy, 40, 0x2a1c16);
      CR(fx, fy, 36, 0x2a1c16);
      C(fx, fy, 34, 0x1f1b18);
      for (int i = 0; i < 26; i++)
      {
        float a = hs(i * 2.2f) * 6.28f, r = hs(i * 1.1f) * 28;
        tuft(fx + cosf(a) * r, fy + sinf(a) * r + 4, i * 9.9f, i % 3 != 0 ? 1 : 0);
      }
      for (int i = 0; i < 5; i++)
      {
        float a = -2.6f + i * 0.5f;
        vine(fx + cosf(a) * 56, fy + sinf(a) * 56, 20, 70 + i, 1);
      }
      gallery(fx + 50, fy - 30, tx + 10, ty + 90, 12, 21);
      tree(214, 420, 18, -1.9f, 4, 3);
      tree(18, 210, 16, -1.2f, 4, 8);
      tree(130, 470, 14, -1.5f, 3, 5);
      for (int i = 0; i < 150; i++)
      {
        float k = hs(i * 9.7f);
        tuft(hs(i * 1.9f) * W, hs(i * 4.3f) * h, i * 2.9f, k < 0.25f ? 2 : k < 0.5f ? 1 : 0);
      }
      foreach (int x in new[] { 8, 58, 108 })
      {
        block(x, 262, 5, 5, 24, 0x7a4422, 0x4a2a1a);
        L(x, 262, x + 5, 238, 0x6b3a1e);
      }
      pipe(Pts(0, 440, 60, 440, 60, 480, 120, 480), 3, PP[RUST], 14, 3, 4, true);
    }
    static void z7a(int h, int t)
    {
      for (int i = 0; i < 4; i++) // 飛ぶ鳥
      {
        float bx = fmodf(t * (0.5f + i * 0.1f) + i * 80, W + 60) - 30, by = 60 + i * 90 + sinf(t * 0.02f + i) * 10;
        float f = (((t >> 3) + i) & 1) != 0 ? -2 : 1;
        Ell(bx + 6.5f, by + 14.5f, 1.5f, 0.7f, 0, 89);
        Seg(bx - 4 + 0.5f, by + f + 0.5f, bx + 0.5f, by + 0.5f, 0.6f, 0x141414);
        Seg(bx + 0.5f, by + 0.5f, bx + 4 + 0.5f, by + f + 0.5f, 0.6f, 0x141414);
        Disc(bx + 0.5f, by + 1, 0.7f, 0x141414);
      }
      for (int i = 0; i < 10; i++)
      {
        float ph = fmod1(t * 0.003f + hs(i));
        Disc(fmodf(hs(i + 3) * W + ph * 90, W) + 0.5f, 30 + hs(i + 9) * (h - 60) - ph * 40 + 0.5f, 0.55f, 0xefe6d0);
      }
    }
    static void z7f(int h)
    {
      bundleH(232, 0, 150, new[] { 4, RUST, 3, RUST, 2, SILVER }, 8, 12);
      pipe(Pts(150, 236, 180, 236, 180, 200), 4, PP[RUST], 14, 8, 12, true);
    }

    // ---- 一覧 (下から順) と、起動時に描いておく ----
    public static bool Build()
    {
      ZONES = new[] {
        new Zone("GRASS YARD", 420, z1, z1a, z1f), new Zone("FOOTINGS", 440, z2, z2a, z2f), new Zone("DUCTS", 520, z3, z3a, z3f),
        new Zone("PIPE MAZE", 520, z8, z8a, z8f), new Zone("PRESSES", 420, z4, z4a, null), new Zone("SETTLING TANK", 440, z5, z5a, null),
        new Zone("GREAT HALL", 520, z6, z6a, z6f), new Zone("FURNACE", 500, z7, z7a, z7f),
      };
      NZ = ZONES.Length;
      STARTS = new int[NZ];
      TOT = 0;
      for (int i = 0; i < NZ; i++)
      {
        STARTS[i] = TOT;
        TOT += ZONES[i].H;
      }
      bgBuf = new int[PW * TOT * S];
      foreBuf = new int[PW * TOT * S];
      foreRow = new bool[TOT * S];
      for (int i = 0; i < NZ; i++)
        BuildZone(i);
      for (int r = 0; r < TOT * S; r++)
      {
        int b = r * PW;
        for (int x = 0; x < PW; x++)
          if (foreBuf[b + x] != 0)
          {
            foreRow[r] = true;
            break;
          }
      }
      return true;
    }

    static void BuildZone(int i)
    {
      Zone z = ZONES[i];
      int row = TOT - STARTS[i] - z.H;
      Surf sb = new Surf();
      sb.Buf = bgBuf;
      sb.Base = row * S * PW;
      sb.Hpx = z.H * S;
      Surf saved = Sf;
      Sf = sb;
      z.Stat(z.H);
      if (z.Fore != null)
      {
        Surf sfo = new Surf();
        sfo.Buf = foreBuf;
        sfo.Base = row * S * PW;
        sfo.Hpx = z.H * S;
        sfo.Keyed = true;
        sfo.Alt = sb;
        Sf = sfo;
        z.Fore(z.H);
      }
      Sf = saved;
    }
  }
}
