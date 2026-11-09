// 敵: 区画ごとの出る表、動き、弱ったときの生き延びる動き、核の乗り移り、描き方。
// 動きは ESP32 版 (game2_enemy.cpp) と同じ。絵は 3 倍の細かさで描き直したもの
using System;
using System.Collections.Generic;

namespace Foundry
{
  // 敵とボスで使う描く部品
  public class Ep : D
  {
    public const uint RED = 0xff3b2a, RED2 = 0xb0201a, RED3 = 0x4a1410, REDW = 0xffe8e0;

    // 赤く光る目 (乗り移ったしるし)
    public static void eye(float x, float y, float r, int t, bool flash, bool weakFlicker)
    {
      if (weakFlicker && ((t >> 1) & 1) != 0) // 弱ると目がちらつく
      {
        Disc(x, y, r + 0.3f, 0x1a0606);
        Disc(x, y, r * 0.6f, 0x3a0a08);
        return;
      }
      Disc(x, y, r + 3.2f, RED, 18);
      Disc(x, y, r + 1.6f, RED, 40);
      Disc(x, y, r + 0.5f, 0x1a0606);
      Disc(x, y, r, flash ? 0xffffffu : RED2);
      Disc(x - r * 0.12f, y - r * 0.12f, r * 0.72f, flash ? 0xffffffu : RED);
      Disc(x - r * 0.28f, y - r * 0.3f, r * 0.32f, 0xffb4a4);
      Q(x - r * 0.5f, y - r * 0.55f, REDW);
    }

    // 機械に這う赤い配線 (光が流れる)。xy は折れ点を並べたもの
    public static void nerve(int t, float ph, params float[] xy)
    {
      float u = 0;
      for (int s = 0; s + 3 < xy.Length; s += 2)
      {
        float x0 = xy[s], y0 = xy[s + 1], x1 = xy[s + 2], y1 = xy[s + 3];
        float l = hypotf(x1 - x0, y1 - y0);
        int n = Math.Max(1, (int)Math.Ceiling(l / 0.34f));
        for (int k = 0; k < n; k++, u += l / n)
        {
          float x = x0 + (x1 - x0) * k / n, y = y0 + (y1 - y0) * k / n;
          float m = fmodf(u / 1.2f - t * 0.6f + ph, 14);
          if (m < 0)
            m += 14;
          if (m < 2)
          {
            Rect(x - 0.35f, y - 0.35f, 0.7f, 0.7f, RED);
            Disc(x, y, 1.3f, RED, 18);
          }
          else
            Rect(x - 0.3f, y - 0.3f, 0.6f, 0.6f, RED3);
        }
      }
    }

    // 弱ったときの火花
    public static void sparks(float x, float y, int t, int n)
    {
      for (int k = 0; k < n; k++)
        if (hs(t * 3.1f + k) > 0.5f)
        {
          float px = x + (hs(t + k) - 0.5f) * 14, py = y + (hs(t * 1.7f + k) - 0.5f) * 14, a = hs(t * 2.3f + k) * 6.28f;
          uint c = k % 2 != 0 ? 0xffe27au : REDW;
          Seg(px, py, px + cosf(a) * 1.4f, py + sinf(a) * 1.4f, 0.5f, c);
          Q(px, py, 0xffffff);
        }
    }
  }

  public partial class Game
  {
    // 敵の種類 (図鑑の順)。空の敵 (TWIN・MAINT・CORE) は床と一緒に流れない。ほかは床に乗っていて、gy に床の位置を持つ
    public const int TWIN = 0,   // ツインファン: 2 つの羽根で浮いて 3 方向に撃つ。弱ると片方の羽根が止まり、端へ逃げる
      WALKER = 1, // ウォーカー: 歩いて止まって 2 発ずつ撃つ。弱ると端へ歩いて逃げる
      BUG = 2,    // ボルトムシ: 群れで這って寄ってくる。群れが半分になると散る
      WELDER = 3, // ウェルダー: 床に固定。溶接の火花を飛ばす。続けて撃たれると腕をたたんで隠れる (弾が効かない)
      FORK = 4,   // フォーク: ふかしてから突進する。弱ると木箱を盾にして下がる
      SNAKE = 5,  // パイプヘビ: 配管の口から出て別の口へ潜る。節を壊すと頭だけで逃げる
      EYE = 6,    // アイ: 壁の防犯カメラ。撃ってはこない。戦車を見つけると仲間を呼ぶ (ツインファンが来る)
      SCRAP = 7,  // ダンゴ: 鉄くずを吸って大きくなる。大きくなると破片を飛ばす
      MAINT = 8,  // メンテ: 傷ついた仲間を直す。狙われると逃げる
      CORE = 9;   // 核: 壊した機械から出て、近くの機械へ逃げる。逃がすとその機械が強くなる

    public class Enemy
    {
      public int k, id, lvl, st;   // lvl: 核が乗り移って強くなった段 (0〜3)。st: 動きの段階
      public float x, y;           // 画面の位置
      public float gx, gy;         // 床の敵は床の位置 (y = gy + scroll)。空の敵は動きの元の位置
      public float vx, vy, a, r;
      public float ux, uy;         // パイプヘビ: 潜る口 (床の位置)
      public int hp = 1, maxhp = 1, t0, hit, tm, cnt, grp, link = -1;
      public int virus;            // 爆弾のウイルスで止まっている残りのフレーム (動かず、撃たず、触れてもやられない)
      public int[] seg = new int[8]; // パイプヘビの節の耐久
      public bool gone, hidden, alerted;
      public float px, py;         // 前のフレームの位置 (あいだを補って描く)
    }

    List<Enemy> en = new List<Enemy>(), born = new List<Enemy>(); // born: このフレームに生まれた敵 (あとで en に足す)
    int evIdx, lap, nextId = 1, nextGrp = 1, pendingLvl;
    float diff; // 難しさ (0〜1)。序盤ほど小さい

    const float SNAKE_DU = 0.05f;         // パイプヘビの節と節の間 (道のりの割合)
    const int VIRUS_FRAMES = 13 * 30;     // ウイルスで止まる長さ (13 秒)

    // ---- 種類ごとの決まり ----
    static readonly int[] BASE_HP = { 3, 5, 1, 6, 10, 8, 2, 4, 2, 1 };
    static readonly int[] POINTS = { 100, 200, 50, 300, 500, 800, 200, 100, 300, 500 };
    static bool isAir(int k) { return k == TWIN || k == MAINT || k == CORE; }
    static bool isMachine(int k) { return k != BUG && k != CORE; } // 核が乗り移れる
    static bool dropsCore(int k) { return k != BUG && k != MAINT && k != CORE; }
    static bool weak(Enemy e) { return e.hp * 3 <= e.maxhp; }
    // 撃つ間。強くなった段 lvl だけ詰め、序盤 (diff が小さい) ほど空ける
    static int period(int bs, int lvl, float diff) { return Math.Max(bs, (int)(bs * (2.2f - 0.7f * diff)) - bs * lvl * 18 / 100); }
    // 難しさ (0〜1)。1 周目は区画ごとに上がり、2 周目からは 1
    static readonly float[] DIFF = { 0.0f, 0.2f, 0.4f, 0.55f, 0.7f, 0.8f, 0.9f, 1.0f };

    // ---- 区画ごとの出る表 ----
    // 区画、区画の下端からの位置 (画面の上端がそこまで来たら出る)、種類、横の位置、数
    const int eTW = TWIN, eWK = WALKER, eBG = BUG, eWD = WELDER, eFK = FORK, eSN = SNAKE, eEY = EYE, eSC = SCRAP, eMT = MAINT;
    const int bGANTRY = 100, bSTAMPER = 101, bHEARTH = 102; // ボス
    static readonly int[,] EVS = {
      // 0 草むらの搬入口: 少なめ。ツインファンとウォーカーから
      {0, 40, eTW, 60, 1}, {0, 110, eWK, 170, 1}, {0, 180, eTW, 150, 2}, {0, 250, eFK, 70, 1}, {0, 310, eBG, 120, 3},
      {0, 370, eTW, 90, 2},
      // 1 基礎の建屋: ボルトムシ (小さめの群れ) とメンテ
      {1, 30, eBG, 60, 4}, {1, 100, eWK, 160, 1}, {1, 160, eTW, 120, 2}, {1, 220, eBG, 170, 5}, {1, 270, eMT, 120, 1},
      {1, 330, eWK, 60, 1}, {1, 390, eTW, 90, 2}, {1, 425, eEY, 22, 1},
      // 2 ダクトと階段: ウェルダー
      {2, 30, eWD, 70, 1}, {2, 80, eTW, 160, 2}, {2, 130, eWD, 180, 1}, {2, 170, eWK, 110, 1}, {2, 220, eTW, 70, 3},
      {2, 260, eEY, 218, 1}, {2, 300, eWD, 120, 1}, {2, 330, eMT, 60, 1}, {2, 370, eWK, 60, 2}, {2, 420, eTW, 150, 2},
      {2, 470, eWD, 60, 1}, {2, 495, eWD, 180, 1},
      // 3 配管の迷路: パイプヘビ
      {3, 40, eSN, 50, 1}, {3, 110, eTW, 170, 2}, {3, 160, eBG, 170, 6}, {3, 220, eSN, 190, 1}, {3, 290, eTW, 60, 3},
      {3, 340, eBG, 70, 6}, {3, 380, eSN, 60, 1}, {3, 450, eTW, 140, 2}, {3, 505, eEY, 22, 1},
      // 4 止まったプレス: ダンゴとウェルダー
      {4, 30, eSC, 80, 1}, {4, 90, eWD, 180, 1}, {4, 140, eWK, 60, 2}, {4, 190, eSC, 170, 1}, {4, 215, bSTAMPER, 0, 1}, {4, 240, eTW, 120, 2},
      {4, 290, eWD, 50, 1}, {4, 330, eMT, 180, 1}, {4, 370, eSC, 110, 1},
      // 5 沈殿槽: 水の上なので空の敵
      {5, 30, eTW, 60, 3}, {5, 90, eTW, 180, 3}, {5, 150, eMT, 120, 1}, {5, 190, eTW, 120, 4}, {5, 250, eEY, 218, 1},
      {5, 290, eTW, 50, 3}, {5, 300, bGANTRY, 0, 1}, {5, 340, eTW, 190, 3}, {5, 400, eMT, 80, 1},
      // 6 大きな建屋: フォークとアイ
      {6, 30, eFK, 120, 1}, {6, 90, eWK, 60, 2}, {6, 140, eEY, 22, 1}, {6, 180, eFK, 180, 1}, {6, 230, eMT, 100, 1},
      {6, 260, eTW, 140, 2}, {6, 310, eWK, 170, 2}, {6, 360, eFK, 60, 1}, {6, 410, eBG, 120, 6}, {6, 450, eEY, 218, 1},
      {6, 480, eFK, 150, 1},
      // 7 溶鉱炉の跡: ダンゴ
      {7, 30, eSC, 60, 1}, {7, 80, eTW, 170, 3}, {7, 130, eWK, 110, 2}, {7, 180, eSC, 180, 1}, {7, 230, eTW, 60, 3},
      {7, 280, eSC, 120, 1}, {7, 330, eMT, 60, 1}, {7, 360, eWK, 170, 2}, {7, 400, bHEARTH, 0, 1}, {7, 420, eTW, 120, 4}, {7, 460, eSC, 70, 1},
    };
    static int NEV { get { return EVS.GetLength(0); } }
    const int LEAD = 16; // 画面の上端より少し上で出す

    static float evPos(int i, int lap) { return (float)lap * Bg.TOT + Bg.STARTS[EVS[i, 0]] + EVS[i, 1]; }

    void resetSpawns(bool skipVisible)
    {
      lap = 0;
      evIdx = 0;
      born.Clear();
      if (skipVisible) // 始めから画面に見えている所の分は出さない
        while (evIdx < NEV && evPos(evIdx, 0) <= scroll + H + LEAD)
          evIdx++;
    }

    void spawnEvents()
    {
      float line = scroll + H + LEAD;
      for (int guard = 0; guard < 8; guard++)
      {
        if (evIdx >= NEV)
        {
          evIdx = 0;
          lap++;
        }
        float p = evPos(evIdx, lap);
        if (p > line)
          break;
        int i = evIdx++;
        spawn(EVS[i, 2], EVS[i, 3], EVS[i, 4], H - p);
      }
    }

    Enemy addEnemy(int k, float x, float y)
    {
      Enemy e = new Enemy();
      born.Add(e);
      e.k = k;
      e.id = nextId++;
      e.x = e.gx = x;
      e.y = y;
      e.t0 = t;
      int lvl = Math.Min(lap, 2);
      if (isMachine(k) && pendingLvl > 0) // 前に逃がした核が乗り移っている
      {
        pendingLvl--;
        lvl++;
      }
      e.lvl = Math.Min(3, lvl);
      int b = BASE_HP[k];
      e.hp = e.maxhp = k == BUG || k == CORE ? b : b + e.lvl * Math.Max(1, b / 2);
      e.px = x;
      e.py = y;
      return e;
    }

    // gyTop: 床の位置 (y = gy + scroll。scroll は小数のまま)
    void spawn(int k, int x, int n, float gyTop)
    {
      if (k >= 100) // ボス
      {
        startBoss(k - 100);
        return;
      }
      float sc = SC;
      gyTop += scroll - sc; // 床の位置は、描くときの流れ (細かい点でそろえたもの) に合わせる
      for (int i = 0; i < n; i++)
      {
        switch (k)
        {
          case TWIN:
            {
              Enemy e = addEnemy(TWIN, clampf(x + (i - (n - 1) * 0.5f) * 36, 22, W - 22), -16 - i * 18);
              e.a = hs(t + i * 7) * 6;
              break;
            }
          case WALKER:
            {
              Enemy e = addEnemy(WALKER, clampf(x + i * 46 - (n - 1) * 23, 20, W - 20), 0);
              e.gy = gyTop - i * 12;
              e.y = e.gy + sc;
              break;
            }
          case BUG:
            {
              Enemy e = addEnemy(BUG, clampf(x + (hs(t + i * 3.3f) - 0.5f) * 40, 10, W - 10), 0);
              e.gy = gyTop - i * 8;
              e.y = e.gy + sc;
              e.grp = nextGrp;
              e.cnt = n;
              break;
            }
          case SNAKE:
            {
              Enemy e = addEnemy(SNAKE, x, 0);
              e.gy = gyTop - 10;
              e.ux = W - x;
              e.uy = gyTop + 50;
              e.r = -0.05f; // 頭の位置 (道のりの割合)。0 で口から出る
              e.hidden = true;
              for (int j = 0; j < 8; j++)
                e.seg[j] = 2;
              break;
            }
          case EYE:
            {
              Enemy e = addEnemy(EYE, x, 0);
              e.gy = gyTop;
              e.y = e.gy + sc;
              e.vx = PIf / 2 + (x < W / 2 ? -0.5f : 0.5f); // 見張る向きの真ん中 (下の、画面の内側)
              break;
            }
          case MAINT:
            addEnemy(MAINT, x, -14);
            break;
          default: // WELDER・FORK・SCRAP
            {
              Enemy e = addEnemy(k, x, 0);
              e.gy = gyTop;
              e.y = e.gy + sc;
              if (k == SCRAP)
                e.r = 7;
              break;
            }
        }
        if (k == SNAKE || k == WELDER || k == FORK || k == EYE || k == SCRAP || k == MAINT)
          break; // 1 つずつ
      }
      if (k == BUG)
        nextGrp++;
    }

    Enemy findId(int id)
    {
      if (id < 0)
        return null;
      foreach (Enemy e in en)
        if (e.id == id && e.hp > 0 && !e.gone)
          return e;
      return null;
    }

    // パイプヘビの道のり u (0〜1) の画面の位置。口 A から、下へふくらみ、くねって、口 B へ
    static void snakeAt(Enemy e, float u, float sc, out float x, out float y)
    {
      float ax = e.gx, ay = e.gy + sc, bx = e.ux, by = e.uy + sc;
      float dx = bx - ax, dy = by - ay, l = Math.Max(1.0f, hypotf(dx, dy));
      float nx = -dy / l, ny = dx / l, w = sinf(u * 2 * PIf) * 22;
      x = clampf(ax + dx * u + nx * w, 8, W - 8);
      y = ay + dy * u + ny * w + sinf(u * PIf) * 70;
    }

    // ---- 動き ----
    void enemyStep()
    {
      float sc = SC;
      diff = lap > 0 ? 1.0f : DIFF[Math.Min(zoneAt(H / 2), 7)];
      spawnEvents();
      en.AddRange(born);
      born.Clear();

      // ボルトムシの群れの残り
      int[] alive = new int[32];
      foreach (Enemy e in en)
        if (e.k == BUG && e.hp > 0)
          alive[e.grp & 31]++;

      foreach (Shot b in shots)
      {
        b.x += b.dx;
        b.y += b.dy;
        if (b.y < -10 || b.x < -10 || b.x > W + 10)
          b.dead = true;
      }
      foreach (Enemy e in en)
      {
        if (e.k == BUG && e.st == 0 && alive[e.grp & 31] * 2 <= e.cnt)
          e.st = 1; // 群れが半分になったので散る
        stepOne(e);
      }

      // 自分の弾が当たる
      foreach (Shot b in shots)
      {
        if (b.dead)
          continue;
        if (bossShot(b.x, b.y))
        {
          b.dead = true;
          continue;
        }
        foreach (Enemy e in en)
        {
          if (e.hp <= 0 || e.gone || (e.hidden && e.k != SNAKE))
            continue;
          int part = -1;
          float dx = b.x - e.x, dy = b.y - e.y;
          switch (e.k)
          {
            case BUG:
              part = fabsf(dx) < 7 && fabsf(dy) < 7 ? 0 : -1;
              break;
            case FORK:
              part = fabsf(dx) < 12 && dy > -16 && dy < 27 ? 0 : -1;
              break;
            case SCRAP:
              part = hypotf(dx, dy) < e.r + 2 ? 0 : -1;
              break;
            case EYE:
            case CORE:
              part = hypotf(dx, dy) < 7 ? 0 : -1;
              break;
            case MAINT:
              part = hypotf(dx, dy) < 9 ? 0 : -1;
              break;
            case SNAKE:
              if (!e.hidden && hypotf(dx, dy) < 8)
                part = 0;
              else
                for (int i = 0; i < 8 && part < 0; i++)
                {
                  float u = e.r - (i + 1) * SNAKE_DU, sx, sy;
                  if (e.seg[i] == 0 || u < 0 || u > 1)
                    continue;
                  snakeAt(e, u, sc, out sx, out sy);
                  if (hypotf(b.x - sx, b.y - sy) < 7)
                    part = i + 1;
                }
              break;
            default: // TWIN・WALKER・WELDER
              part = fabsf(dx) < 11 && fabsf(dy) < 11 ? 0 : -1;
              break;
          }
          if (part >= 0)
          {
            b.dead = true;
            hitEnemy(e, part, b.y);
            break;
          }
        }
      }
      en.AddRange(born); // 壊れたところから出た核
      born.Clear();
      en.RemoveAll(e => e.hp <= 0 || e.gone);

      foreach (Shot b in eb)
      {
        b.x += b.dx;
        b.y += b.dy;
        if (b.life > 0 && --b.life == 0)
          b.dead = true;
        if (b.y > H + 8 || b.y < -8 || b.x < -8 || b.x > W + 8)
          b.dead = true;
      }
      foreach (Fx f in fx)
      {
        f.t++;
        if (f.ground)
          f.y += spd;
      }
      fx.RemoveAll(f => f.t > f.life);
    }

    void shoot(float x, float y, float a, float s, int k, int life)
    {
      if (eb.Count < 14 + (int)(8 * diff)) // 画面の敵の弾は多くなりすぎないように
      {
        eb.Add(new Shot(x, y, cosf(a) * s, sinf(a) * s, k, life));
        if (k != 3)
          sfx(Sfx.EnemyShot, 0.5f);
      }
    }

    void stepOne(Enemy e)
    {
      float sc = SC;
      int age = t - e.t0;
      bool canShoot = tankOn && enterT == 0 && e.y > 0 && e.y < H - 60;
      float sp = 1.5f + 0.5f * diff + 0.25f * e.lvl; // 弾の速さ (序盤は遅い)
      float toT = atan2f(ty - e.y, tx - e.x), dist = hypotf(tx - e.x, ty - e.y);
      Func<int, bool> tick = bs => age > 20 && (age + e.id * 7) % period(bs, e.lvl, diff) == 0;
      if (e.hit > 0)
        e.hit--;
      if (e.virus > 0) // ウイルスで止まっている: 床の敵は床と一緒に流れるだけ、空の敵はゆっくり落ちる
      {
        e.virus--;
        e.t0++; // 動きの時間も止める
        if (e.k == SNAKE)
          snakeAt(e, clampf(e.r, 0, 1), sc, out e.x, out e.y);
        else if (isAir(e.k))
          e.y += 0.3f;
        else
        {
          e.x = e.gx;
          e.y = e.gy + sc;
        }
        if (e.k == TWIN)
          e.gx = e.x - 26 * sinf((t - e.t0) * 0.03f + e.a); // 動き出したときに跳ばないように
        if (e.y > H + 40)
          e.gone = true;
        return;
      }

      switch (e.k)
      {
        case TWIN:
          if (e.st == 0)
          {
            e.y += 0.9f;
            e.x = e.gx + 26 * sinf(age * 0.03f + e.a);
            if (tick(70) && canShoot)
              foreach (float d in new[] { -0.35f, 0.0f, 0.35f })
                shoot(e.x, e.y + 4, toT + d, sp, 0, 0);
            if (weak(e)) // 片方の羽根が止まって、近い端へ逃げる
            {
              e.st = 1;
              e.vx = e.x < W / 2 ? -1.6f : 1.6f;
            }
          }
          else
          {
            e.x += e.vx;
            e.y += 0.25f;
            if (e.x < -24 || e.x > W + 24)
              e.gone = true;
          }
          break;

        case WALKER:
          {
            if (e.st == 0)
            {
              bool walking = age % 120 < 70;
              if (walking)
                e.gy += 0.35f;
              int c = age % 120;
              if (canShoot && ((c == 80 && ((age / 120 + e.id) % 2 == 0 || e.lvl > 0)) || (e.lvl > 1 && c == 100))) // 止まったとき、2 回に 1 回撃つ
                foreach (float o in new[] { -2.0f, 2.0f })
                  shoot(e.x + o, e.y + 8, toT, sp, 0, 0);
              if (weak(e)) // 端の物陰へ歩いて隠れる
                e.st = 1;
            }
            else
            {
              e.gx += e.gx < W / 2 ? -0.9f : 0.9f;
              if (e.gx < -20 || e.gx > W + 20)
                e.gone = true;
            }
            e.x = e.gx;
            e.y = e.gy + sc;
            break;
          }

        case BUG:
          {
            float dx = 0, dy = 1;
            if (tankOn)
            {
              dx = cosf(toT);
              dy = sinf(toT);
            }
            float s = 0.75f;
            if (e.st == 1) // 散り散りに逃げる
            {
              dx = -dx;
              dy = -dy;
              s = 1.8f;
            }
            float w = sinf(age * 0.15f + e.id) * 0.6f;
            float mx = (dx - dy * w) * s, my = (dy + dx * w) * s;
            e.gx += mx;
            e.gy += my - (e.st == 0 ? 0 : 0.28f);
            e.a = atan2f(my + spd, mx);
            e.x = e.gx;
            e.y = e.gy + sc;
            if (e.st == 1 && (e.x < -12 || e.x > W + 12 || e.y < -12))
              e.gone = true;
            break;
          }

        case WELDER:
          {
            e.x = e.gx;
            e.y = e.gy + sc;
            if (age % 25 == 0 && e.cnt > 0)
              e.cnt--; // 続けて撃たれた数は少しずつ忘れる
            if (e.st == 1) // 腕をたたんで隠れている
            {
              if (--e.tm <= 0)
                e.st = 0;
              break;
            }
            float aim = clampf(tankOn ? toT : PIf / 2, 0.3f, PIf - 0.3f);
            if (age < 2)
              e.a = aim;
            e.a += (aim + 0.4f * sinf(age * 0.05f) - e.a) * 0.08f;
            if (tick(50) && canShoot)
            {
              float a2 = e.a + 0.5f * sinf(age * 0.05f + 1);
              float tipx = e.x + cosf(e.a) * 14 + cosf(a2) * 12, tipy = e.y + sinf(e.a) * 14 + sinf(a2) * 12;
              shoot(tipx, tipy, atan2f(ty - tipy, tx - tipx), sp + 0.3f, 1, 0);
              e.tm = 6; // 先の火花
            }
            else if (e.tm > 0)
              e.tm--;
            break;
          }

        case FORK:
          {
            if (e.st == 0) // エンジンをふかす
            {
              if (e.tm == 0 && e.y > 0)
                sfx(Sfx.Growl, 0.35f);
              if (++e.tm >= 40)
              {
                e.st = 1;
                e.tm = 0;
                e.r = 0;
                e.a = clampf(tankOn ? toT : PIf / 2, PIf / 2 - 0.5f, PIf / 2 + 0.5f);
              }
            }
            else if (e.st == 1) // 一直線に突進
            {
              e.r = Math.Min(3.0f + 2.0f * diff, e.r + 0.35f); // 序盤は突進が遅い
              e.gx = clampf(e.gx + cosf(e.a) * e.r, 14, W - 14);
              e.gy += sinf(e.a) * e.r;
              if (++e.tm >= 22)
              {
                e.st = 2;
                e.tm = 0;
              }
            }
            else if (e.st == 2) // ゆっくり下がる
            {
              e.gy -= 0.75f;
              if (++e.tm >= 60)
              {
                e.st = 0;
                e.tm = 0;
              }
            }
            else // 3: 木箱を盾にして下がる
              e.gy -= 0.7f;
            if (e.st != 3 && e.hp * 5 <= e.maxhp * 2)
            {
              e.st = 3;
              e.cnt = 4; // 木箱の耐久
            }
            e.x = e.gx;
            e.y = e.gy + sc;
            break;
          }

        case SNAKE:
          {
            if (e.gy + sc > 30) // 口が画面に入ってから出てくる
              e.r += e.st == 1 ? 0.02f : 0.006f;
            float u = clampf(e.r, 0, 1);
            snakeAt(e, u, sc, out e.x, out e.y);
            e.hidden = e.r < 0 || e.r > 1;
            bool any = false;
            for (int i = 0; i < 8; i++)
              if (e.seg[i] != 0 && e.r - (i + 1) * SNAKE_DU <= 1)
                any = true;
            if (e.r > 1 && (!any || e.st == 1))
              e.gone = true; // 口 B へ潜りきった
            if (e.gy + sc > H + 140)
              e.gone = true;
            if (!e.hidden && e.st == 0 && dist < 70 && canShoot && age % 50 == 0) // 頭のバルブから蒸気
            {
              foreach (float d in new[] { -0.3f, 0.0f, 0.3f })
                shoot(e.x, e.y, toT + d, 2.2f, 3, 18);
              sfx(Sfx.Steam);
            }
            break;
          }

        case EYE:
          {
            e.x = e.gx;
            e.y = e.gy + sc;
            if (e.st == 0) // 首を振って見張る
            {
              e.r += 0.03f;
              e.a = e.vx + 0.9f * sinf(e.r);
              if (tankOn && enterT == 0 && e.y > 0 && dist < 230 && fabsf(angDiff(toT, e.a)) < 0.28f)
              {
                e.st = 1;
                e.tm = 0;
                sfx(Sfx.Alarm);
                if (!e.alerted) // 仲間に知らせる: ツインファンが 2 機来る
                {
                  e.alerted = true;
                  foreach (int s in new[] { -1, 1 })
                  {
                    Enemy n = addEnemy(TWIN, clampf(tx + s * 50, 22, W - 22), -16 - (s > 0 ? 20 : 0));
                    n.a = hs(t + s) * 6;
                  }
                }
              }
            }
            else if (e.st == 1) // 見つけた: 戦車を目で追いながら、赤く点滅して知らせる (撃ってはこない)
            {
              e.a += clampf(angDiff(toT, e.a), -0.04f, 0.04f);
              if (++e.tm >= 45)
              {
                e.st = 3;
                e.tm = 0;
              }
            }
            else if (++e.tm >= 40)
            {
              e.st = 0;
              e.r = asinf(clampf((e.a - e.vx) / 0.9f, -1, 1));
            }
            break;
          }

        case SCRAP:
          {
            e.r = Math.Min(18.0f, e.r + 11.0f / 300);
            e.maxhp = 4 + (int)((e.r - 7) * 12 / 11) + e.lvl * 2;
            e.hp = e.maxhp - e.cnt;
            if (tankOn)
              e.gx += clampf(tx - e.gx, -0.3f, 0.3f);
            e.gy += 0.25f;
            e.a += 0.03f;
            e.x = e.gx;
            e.y = e.gy + sc;
            if (e.r > 14 && tick(40) && canShoot) // 大きくなると破片を飛ばす
              foreach (float d in new[] { -0.35f, 0.0f, 0.35f })
                shoot(e.x, e.y, toT + d, sp, 0, 0);
            break;
          }

        case MAINT:
          {
            if (e.st == 0)
            {
              foreach (Shot b in shots) // 狙われたら全力で逃げる
                if (!b.dead && hypotf(b.x - e.x, b.y - e.y) < 34)
                {
                  e.st = 1;
                  e.tm = 30;
                  float a = atan2f(e.y - ty, e.x - tx) + (hs(t + e.id) - 0.5f);
                  e.vx = cosf(a) * 2.6f;
                  e.vy = sinf(a) * 2.6f;
                  break;
                }
            }
            if (e.st == 1)
            {
              e.x += e.vx;
              e.y += e.vy;
              if (--e.tm <= 0)
                e.st = 0;
              if (e.x < -20 || e.x > W + 20 || e.y < -30)
                e.gone = true;
              break;
            }
            // いちばん傷ついた仲間に付く
            Enemy tg = findId(e.link);
            if (tg == null || tg.hp >= tg.maxhp)
            {
              tg = null;
              float worst = 1;
              foreach (Enemy o in en)
                if (o.hp > 0 && !o.gone && isMachine(o.k) && o.k != MAINT && o.y > 0 && o.y < H && o.hp < o.maxhp)
                {
                  float q = (float)o.hp / o.maxhp;
                  if (q < worst)
                  {
                    worst = q;
                    tg = o;
                  }
                }
              e.link = tg != null ? tg.id : -1;
            }
            if (tg != null)
            {
              float gx = tg.x + 14, gy = tg.y - 16, d = hypotf(gx - e.x, gy - e.y);
              if (d > 1)
              {
                float s = Math.Min(1.6f, d);
                e.x += (gx - e.x) / d * s;
                e.y += (gy - e.y) / d * s;
              }
              if (d < 10 && ++e.tm >= 40)
              {
                e.tm = 0;
                tg.hp = Math.Min(tg.maxhp, tg.hp + 1);
                if (tg.k == SCRAP)
                  tg.cnt = Math.Max(0, tg.cnt - 1);
                e.cnt = 10; // 直した光
              }
            }
            else
            {
              e.x += sinf(age * 0.04f) * 0.6f;
              e.y += 0.6f;
            }
            if (e.cnt > 0)
              e.cnt--;
            break;
          }

        case CORE:
          {
            if (e.st == 0 && e.link == -2) // 炉心に呼ばれた核: 炉心の目へ。着いたら炉心が直る
            {
              if (boss.k != 2 || boss.dieT > 0)
                e.st = 1;
              else
              {
                float gx = bossEyeX(), gy = bossEyeY(), d = hypotf(gx - e.x, gy - e.y);
                float ux = (gx - e.x) / Math.Max(d, 1.0f), uy = (gy - e.y) / Math.Max(d, 1.0f), z = cosf(age * 0.3f) * 1.6f;
                e.vx = ux * 1.5f - uy * z;
                e.vy = uy * 1.5f + ux * z;
                e.x += e.vx;
                e.y += e.vy;
                if (d < 8)
                {
                  boss.hp[0] = Math.Min(boss.maxhp[0], boss.hp[0] + 6);
                  sfx(Sfx.Possess, 0.8f);
                  fx.Add(new Fx(4, gx, gy, 0, 0, 16, false));
                  e.gone = true;
                }
              }
              break;
            }
            if (e.st == 0)
            {
              Enemy tg = findId(e.link);
              if (tg == null)
              {
                float bd = 260;
                foreach (Enemy o in en)
                  if (o.hp > 0 && !o.gone && o.virus == 0 && isMachine(o.k) && o.y > -10 && o.y < H + 10)
                  {
                    float d = hypotf(o.x - e.x, o.y - e.y);
                    if (d < bd)
                    {
                      bd = d;
                      tg = o;
                    }
                  }
                e.link = tg != null ? tg.id : -1;
              }
              if (tg == null || age > 260)
                e.st = 1; // 乗り移れる機械がない。上へ逃げる
              else
              {
                float d = hypotf(tg.x - e.x, tg.y - e.y), ux = (tg.x - e.x) / Math.Max(d, 1.0f), uy = (tg.y - e.y) / Math.Max(d, 1.0f);
                float z = cosf(age * 0.3f) * 1.6f; // ジグザグ
                e.vx = ux * 1.7f - uy * z;
                e.vy = uy * 1.7f + ux * z;
                e.x += e.vx;
                e.y += e.vy;
                if (d < 7) // 乗り移った: その機械が強くなる
                {
                  int inc = Math.Max(1, BASE_HP[tg.k] / 2);
                  tg.lvl = Math.Min(3, tg.lvl + 1);
                  tg.maxhp += inc;
                  tg.hp += inc;
                  if (tg.k == SCRAP)
                    tg.cnt = Math.Max(0, tg.cnt - inc);
                  if (tg.k == WALKER || tg.k == TWIN || tg.k == FORK)
                    tg.st = Math.Min(tg.st, tg.k == FORK ? 2 : 0); // 弱って逃げていたのをやめる
                  fx.Add(new Fx(4, tg.x, tg.y, 0, 0, 16, !isAir(tg.k)));
                  sfx(Sfx.Possess);
                  e.gone = true;
                }
              }
            }
            if (e.st == 1)
            {
              e.vx = sinf(age * 0.3f) * 1.2f;
              e.vy = -2;
              e.x += e.vx;
              e.y += e.vy;
              if (e.y < -16)
              {
                e.gone = true;
                sfx(Sfx.Possess, 0.4f);
                if (diff >= 0.4f) // 序盤は逃がしても強くならない
                  pendingLvl = Math.Min(3, pendingLvl + 1); // 次に出る機械に乗り移る (たまるのは 3 つまで)
              }
            }
            break;
          }
      }
      if (!e.gone && e.k != SNAKE && (e.y > H + 40 || e.y < -110 || e.x < -60 || e.x > W + 60))
        e.gone = true;
    }

    void hitEnemy(Enemy e, int part, float by)
    {
      float sc = SC;
      Action<float, float> spark = (x, y) =>
      {
        fx.Add(new Fx(3, x, y, hs(t) * 6, 0, 5, false));
        sfx(Sfx.Ping);
      };
      if (e.k == WELDER && e.st == 1) // たたんだ腕に弾が効かない
      {
        spark(e.x, e.y - 4);
        return;
      }
      if (e.k == FORK && e.st == 3 && e.cnt > 0 && by > e.y + 10) // 木箱が受け止める
      {
        spark(e.x + (hs(t) - 0.5f) * 14, e.y + 16);
        if (--e.cnt == 0)
          fx.Add(new Fx(5, e.x, e.y + 20, hs(t) * 6, 0, 14, true));
        return;
      }
      if (e.k == SNAKE && part > 0) // 節が壊れたら、残りの節を切り捨てて頭だけで逃げる
      {
        int i = part - 1;
        e.hit = 3;
        if (--e.seg[i] > 0)
          return;
        for (int j = 0; j < 8; j++)
        {
          float u = e.r - (j + 1) * SNAKE_DU, sx, sy;
          if (e.seg[j] == 0 && j != i)
            continue;
          if (u >= 0 && u <= 1)
          {
            snakeAt(e, u, sc, out sx, out sy);
            fx.Add(new Fx(5, sx, sy, hs(t + j) * 6, -j * 2, 14, true));
            if (mode == Mode.Play)
              Score += 50;
          }
          e.seg[j] = 0;
        }
        sfx(Sfx.Flak);
        e.st = 1;
        return;
      }
      e.hit = 3;
      if (e.k == SCRAP)
      {
        e.cnt++;
        e.hp = e.maxhp - e.cnt;
      }
      else
        e.hp--;
      if (e.hp > 0)
        sfx(Sfx.MomHit, 0.3f);
      if (e.k == WELDER && e.hp > 0 && ++e.cnt >= 3) // 続けて撃たれたので隠れる
      {
        e.st = 1;
        e.tm = 75;
        e.cnt = 0;
      }
      if (e.hp <= 0)
        killEnemy(e);
    }

    void killEnemy(Enemy e)
    {
      e.hp = 0;
      int p = POINTS[e.k];
      if (e.k == SCRAP)
        p = 100 + (int)((e.r - 7) * 40);
      if (mode == Mode.Play)
        Score += p * (2 + e.lvl) / 2;
      bool ground = !isAir(e.k) && e.k != EYE;
      if (e.k == CORE)
        sfx(Sfx.CoreKill);
      else if (e.k == BUG || e.k == MAINT)
        sfx(Sfx.Flak, 0.7f);
      else
        sfx(Sfx.G2Boom, e.k == FORK || e.k == SCRAP || e.k == SNAKE ? 1 : 0.7f);
      if (e.k == BUG || e.k == CORE || e.k == MAINT)
        fx.Add(new Fx(5, e.x, e.y, hs(t) * 6, 0, 16, ground));
      else
        fx.Add(new Fx(0, e.x, e.y, hs(t) * 6, 0, 26, ground || e.k == EYE));
      if (e.k == FORK || e.k == SCRAP || e.k == SNAKE)
        fx.Add(new Fx(0, e.x + 8, e.y - 6, hs(t + 3) * 6, -6, 26, true));
      if (dropsCore(e.k)) // 赤い核が飛び出す
      {
        Enemy c = addEnemy(CORE, e.x, e.y);
        c.link = -1;
        c.lvl = 0;
        sfx(Sfx.CoreFree);
      }
      // 弾を強くする部品: 大きい機械 (フォーク・ウェルダー・パイプヘビ) と、部品を運ぶメンテが落とす。ほかは 15 体ごと
      bool every = e.k != CORE && e.k != BUG && ++killCount % 15 == 0;
      if (e.k == FORK || e.k == WELDER || e.k == SNAKE || e.k == MAINT || every)
        dropItem(e.x, e.y);
    }

    // 爆弾の爆発: まわり (半径 BLAST) の敵に 2 発ぶんの傷。上から落ちるので、フォークの木箱は効かない
    void bombBlast(float x, float y)
    {
      const float BLAST = 38;
      float sc = SC;
      fx.Add(new Fx(6, x, y, 0, 0, 14, true));
      sfx(Sfx.BigBoom, 0.6f);
      sfx(Sfx.Virus);
      fx.Add(new Fx(0, x, y, hs(t) * 6, 0, 26, true));
      for (int i = 0; i < 3; i++)
        fx.Add(new Fx(5, x + (hs(t + i) - 0.5f) * 40, y + (hs(t * 3 + i) - 0.5f) * 30, hs(t + i * 5) * 6, -3 - i * 3, 14, true));
      int n = en.Count; // 傷で生まれた核は born に入るので、en は増えない
      for (int i = 0; i < n; i++)
      {
        Enemy e = en[i];
        if (e.hp <= 0 || e.gone || e.k == CORE)
          continue;
        if (e.k == SNAKE)
        {
          for (int j = 0; j < 8; j++)
          {
            float u = e.r - (j + 1) * SNAKE_DU, sx, sy;
            if (e.seg[j] == 0 || u < 0 || u > 1)
              continue;
            snakeAt(e, u, sc, out sx, out sy);
            if (hypotf(sx - x, sy - y) < BLAST)
            {
              hitEnemy(e, j + 1, sy);
              break; // 節が 1 つ壊れると、残りは切り捨てられる
            }
          }
          if (e.hidden)
            continue;
        }
        float r = e.k == SCRAP ? e.r : 8;
        if (hypotf(e.x - x, e.y - y) > BLAST + r)
          continue;
        for (int k = 0; k < 2 && e.hp > 0; k++)
          hitEnemy(e, 0, e.y);
        if (e.hp > 0 && e.k != CORE) // 生き残った機械には、ウイルスを仕掛ける
          e.virus = VIRUS_FRAMES;
      }
      bossBomb(x, y);
    }

    void dropItem(float x, float y)
    {
      if (items.Count < 3)
      {
        Item it = new Item();
        it.x = clampf(x, 16, W - 16);
        it.y = y;
        items.Add(it);
      }
    }

    bool touchesTank(Enemy e)
    {
      if (e.hp <= 0 || e.gone || e.virus > 0)
        return false;
      float dx = e.x - tx, dy = e.y - ty;
      switch (e.k)
      {
        case BUG:
          return fabsf(dx) < 8 && fabsf(dy) < 8;
        case WALKER:
          return fabsf(dx) < 14 && fabsf(dy) < 14;
        case WELDER:
          return fabsf(dx) < 12 && fabsf(dy) < 12;
        case FORK:
          return fabsf(dx) < 14 && dy > -30 && dy < 18; // 車体と爪 (爪は下に出ている)
        case SCRAP:
          return hypotf(dx, dy) < e.r + 6;
        case SNAKE:
          {
            if (!e.hidden && hypotf(dx, dy) < 9)
              return true;
            float sc = SC;
            for (int i = 0; i < 8; i++)
            {
              float u = e.r - (i + 1) * SNAKE_DU, sx, sy;
              if (e.seg[i] == 0 || u < 0 || u > 1)
                continue;
              snakeAt(e, u, sc, out sx, out sy);
              if (hypotf(sx - tx, sy - ty) < 8)
                return true;
            }
            return false;
          }
        default:
          return false;
      }
    }

    // ================= 敵の絵 =================
    static readonly uint[] TAN = { 0x1e1810, 0x3e3426, 0x5e503e, 0x7e6e56, 0x9c8a6e, 0xbaa888, 0xd8c8a8 };
    static readonly uint[] TANF = { 0x3e3426, 0x7e6e56, 0xa8987e, 0xd8d0c0, 0xf0e8d8, 0xfffaf0 };
    static readonly uint[] YELR = { 0x24180a, 0x4e3a12, 0x7a5e1a, 0xa07a26, 0xc29a34, 0xdcbc58, 0xf2dc94 };
    static readonly uint[] ORNG = { 0x220e06, 0x4e260e, 0x7a3e14, 0xa05a1c, 0xc27a34, 0xdc9c5a, 0xf2c898 };
    static readonly uint[] RUSTR = { 0x160a06, 0x2e170d, 0x4e2814, 0x6b3a1e, 0x8a4f28, 0xa8683a, 0xd09a6a };
    static readonly uint[] WHT = { 0x34322c, 0x5e5a50, 0x8a867a, 0xb4b0a4, 0xd6d2c6, 0xf2efe6, 0xffffff };
    static readonly uint[] DARKR = { 0x0a0405, 0x200a08, 0x3a1410, 0x5a1c14 };

    static uint[] Flash(uint[] ramp, bool on)
    {
      if (!on)
        return ramp;
      uint[] o = new uint[ramp.Length];
      for (int i = 0; i < o.Length; i++)
        o[i] = Mix(ramp[i], 0xffffff, 0.55f);
      return o;
    }

    // 回る羽根 (ぶれた円盤と 2 枚の羽根)
    static void rotor(float rx, float ry, float r, float a, bool stop, uint blade)
    {
      if (!stop)
      {
        Disc(rx, ry, r - 0.6f, 0xb8c0c8, 34);
        for (int g = 1; g <= 3; g++) // 残像
          for (int k = 0; k < 2; k++)
          {
            float b = a - g * 0.28f + k * PIf;
            Seg(rx, ry, rx + cosf(b) * (r - 0.8f), ry + sinf(b) * (r - 0.8f), 1.1f, blade, 70 - g * 18);
          }
      }
      for (int k = 0; k < 2; k++)
      {
        float b = a + k * PIf, ca = cosf(b), sa = sinf(b), w = 0.9f;
        float ex = rx + ca * (r - 0.8f), ey = ry + sa * (r - 0.8f);
        Poly(blade, 255, rx - sa * 0.4f, ry + ca * 0.4f, ex - sa * w, ey + ca * w, ex + sa * w * 0.3f, ey - ca * w * 0.3f, rx + sa * 0.4f, ry - ca * 0.4f);
        Q(ex - ca * 0.6f, ey - sa * 0.6f, 0xffffff);
      }
    }

    static void drawTwin(float x, float y, int t, bool flash, bool hurt, bool broken)
    {
      float tilt = broken ? 3 : 0; // 止まった羽根の側が下がる
      Ell(x + 9, y + 18, 9, 6, 0, 60);
      for (int s = -1; s <= 1; s += 2)
      {
        float rx = x + s * 11, ry = y + (s > 0 ? tilt : -tilt * 0.3f);
        bool stop = broken && s > 0;
        Rod(x + s * 3, y, rx - s * 5.5f, ry, 1.1f, DSTEEL, 0.4f);
        // 羽根の囲い
        Ring(rx, ry, 7.0f, 1.6f, 0x3a3d40);
        Ring(rx, ry, 7.3f, PX * 2, 0x9aa0a6);
        Ring(rx - 0.25f, ry - 0.25f, 6.4f, PX, 0x6b7075);
        rotor(rx, ry, 6.2f, stop ? 0.7f : t * 0.9f * s, stop, stop ? 0x8a8f94u : 0xc4c9ccu);
        Ball(rx, ry, 1.5f, STEEL, 0.6f);
        // 囲いの支え
        for (int k = 0; k < 3; k++)
        {
          float b = k * 2.094f + 0.5f;
          Line1(rx + cosf(b) * 1.5f, ry + sinf(b) * 1.5f, rx + cosf(b) * 6.4f, ry + sinf(b) * 6.4f, 0x2a2c2e);
        }
      }
      // 胴体 (丸い機関の箱)
      Ell(x + 0.6f, y + 1.4f, 5.6f, 5.2f, 0x141516);
      Ball(x, y, 4.9f, Flash(STEEL, flash), 0.6f);
      Plate(x - 2.2f, y - 6.6f, 4.4f, 3.2f, Flash(GUN, flash), true, 0.6f, 0.4f); // 上の受信機
      Q(x - 1.2f, y - 6.1f, 0xc8ced3);
      Ring(x, y, 3.6f, PX, 0x3a3e42);
      Disc(x, y + 2.1f, 2.5f, 0x1a0606);
      Ep.eye(x, y + 2.1f, 1.9f, t, flash, hurt);
      Ep.nerve(t, 0, x - 3, y + 4, x - 4, y + 9);
      Ep.nerve(t, 6, x + 3, y + 4, x + 4, y + 9);
    }

    static readonly int[,] WLEG = { { -1, -1, 0 }, { 1, -1, 1 }, { -1, 1, 1 }, { 1, 1, 0 } };
    static void drawWalker(float x, float y, int t, int at, bool flash, bool hurt)
    {
      Ell(x + 3, y + 4, 12, 10, 0, 85);
      for (int i = 0; i < 4; i++)
      {
        float st = sinf(at * 0.2f + WLEG[i, 2] * PIf) * 2;
        float sx = WLEG[i, 0], sy = WLEG[i, 1];
        float hx = x + sx * 6, hy = y + sy * 4, kx = x + sx * 11, ky = y + sy * 4 - 3, fx = x + sx * 13, fy = y + sy * 10 + st;
        Rod(kx, ky, fx, fy, 0.9f, DSTEEL, 0.3f);
        Plate(fx - 2.2f, fy - 1.0f, 4.4f, 2.2f, DSTEEL, true, 0.4f, 0.25f);
        Rod(hx, hy, kx, ky, 1.5f, STEEL, 0.4f);
        Ball(kx, ky, 1.5f, STEEL, 0.6f);
      }
      uint[] body = Flash(TAN, flash);
      Plate(x - 8, y - 8, 16, 15, body, true, 0.62f, 0.4f);
      // 汚れとさび
      Disc(x + 4, y + 3, 2.2f, 0x6b3a1e);
      Disc(x + 4.4f, y + 3.4f, 1.3f, 0x8a4f28);
      Disc(x - 5, y + 4.5f, 0.9f, 0x8a4f28);
      Rect(x - 7, y + 5.6f, 14, PX, 0x3e3426);
      for (int k = 0; k < 3; k++) // 脇の放熱の溝
        Rect(x - 7.2f, y - 5 + k * 1.6f, 2.0f, PX, 0x3e3426);
      // 上のふた (感知器の箱)
      Plate(x - 6.2f, y - 12.2f, 12.4f, 4.4f, Flash(GUN, flash), true, 0.6f, 0.4f);
      for (int k = 0; k < 4; k++)
        Q(x - 4.6f + k * 3.1f, y - 11.4f, 0xc8ced3);
      // 顔の板
      Plate(x - 4.2f, y - 4.2f, 8.4f, 8.4f, GUN, true, 0.5f, 0.3f);
      // 2 本の銃身
      Rod(x - 2, y + 3.4f, x - 2, y + 9.6f, 0.95f, GUN, 0.6f);
      Rod(x + 2, y + 3.4f, x + 2, y + 9.6f, 0.95f, GUN, 0.6f);
      Disc(x - 2, y + 9.8f, 0.45f, 0x050505);
      Disc(x + 2, y + 9.8f, 0.45f, 0x050505);
      Ep.eye(x, y - 1, 2, t, flash, hurt);
      // 立てたアンテナと点滅する灯
      Seg(x + 5.4f, y - 8.2f, x + 7.4f, y - 14.4f, 0.5f, 0x2a2c2e);
      bool on = ((t >> 3) & 1) != 0;
      Disc(x + 7.5f, y - 14.8f, 0.8f, on ? Ep.RED : 0x7a1a10u);
      if (on)
        Disc(x + 7.5f, y - 14.8f, 2.0f, Ep.RED, 60);
      Ep.nerve(t, 0, x - 8, y - 2, x - 12, y + 2);
      Ep.nerve(t, 6, x + 8, y - 2, x + 12, y + 2);
    }

    static void drawBug(float x, float y, float a, int t, int i, bool flash)
    {
      float ca = cosf(a), sa = sinf(a);
      Ell(x + 1.2f, y + 2.2f, 5.4f, 4.4f, 0, 85);
      // 6 本の脚 (左右 3 本ずつ、交互に動く)
      for (int k = 0; k < 6; k++)
      {
        int s = k < 3 ? -1 : 1, f = k % 3 - 1;
        float ph = sinf(t * 0.5f + i + ((k % 2) ^ (k < 3 ? 0 : 1)) * PIf);
        float lx = f * 2.4f, ly = s * 3, mx = f * 3.4f + ph * 0.8f, my = s * 5.6f, ex = f * 4.2f + ph * 1.4f, ey = s * 7.2f;
        float LXw = x + lx * ca - ly * sa, LYw = y + lx * sa + ly * ca, MXw = x + mx * ca - my * sa, MYw = y + mx * sa + my * ca, EXw = x + ex * ca - ey * sa, EYw = y + ex * sa + ey * ca;
        Seg(LXw, LYw, MXw, MYw, 0.6f, 0x2a2c2e);
        Line1(MXw, MYw, EXw, EYw, 0x2a2c2e);
        Q(EXw, EYw, 0x8a8f94);
      }
      uint[] shell = Flash(new uint[] { 0x141516, 0x2e3236, 0x4a5056, 0x6a7178, 0x8c939a, 0xb4bac0, 0xe0e4e8 }, flash);
      Ball(x - ca * 1.2f, y - sa * 1.2f, 4.6f, shell, 0.8f);
      // 背の割れ目 (羽のふた) とボルトの頭
      Line1(x - ca * 0.6f, y - sa * 0.6f, x - ca * 5.2f, y - sa * 5.2f, 0x1a1b1c);
      Disc(x - ca * 2.2f, y - sa * 2.2f, 1.6f, 0x2e3236);
      Disc(x - ca * 2.4f, y - sa * 2.4f, 1.1f, 0x6a7178);
      Line1(x - ca * 2.4f - sa * 0.9f, y - sa * 2.4f + ca * 0.9f, x - ca * 2.4f + sa * 0.9f, y - sa * 2.4f - ca * 0.9f, 0x1a1b1c);
      // 頭
      Ball(x + ca * 2.6f, y + sa * 2.6f, 2.3f, GUN, 0.5f);
      Ep.eye(x + ca * 3.0f, y + sa * 3.0f, 1.05f, t, flash, false);
      // 触角
      for (int s = -1; s <= 1; s += 2)
        Line1(x + ca * 4.2f - sa * s * 0.8f, y + sa * 4.2f + ca * s * 0.8f, x + ca * 6.2f - sa * s * 2.0f, y + sa * 6.2f + ca * s * 2.0f, 0x55585b);
      // しっぽの配線
      Seg(x - ca * 4.6f, y - sa * 4.6f, x - ca * 8 + sinf(t * 0.3f + i), y - sa * 8, 0.5f, Ep.RED3);
    }

    static void drawWelder(float x, float y, float a1, float a2, bool folded, bool tipFlash, int t, bool flash, bool hurt)
    {
      block(x - 11, y - 1, 22, 14, 6, 0x5a5e62, 0x3a3d40);
      // 台の上のボルトと警告のすじ
      for (int k = 0; k < 4; k++)
      {
        float bx = x - 9.5f + k * 6.3f;
        Disc(bx, y - 6, 0.55f, 0x2a2c2e);
        Q(bx - 0.3f, y - 6.3f, 0xb0b6bb);
      }
      for (float k = 0; k < 20; k += 2.6f)
        Poly(0xc9a227, 255, x - 10 + k, y + 5.2f, x - 9 + k, y + 5.2f, x - 9.8f + k, y + 6.6f, x - 10.8f + k, y + 6.6f);
      float Ex, Ey, Tx, Ty;
      if (folded) // 腕をたたんで、土台の上に伏せる
      {
        Ex = x + 7;
        Ey = y + 3;
        Tx = x - 2;
        Ty = y + 5;
      }
      else
      {
        Ex = x + cosf(a1) * 14;
        Ey = y + sinf(a1) * 14;
        Tx = Ex + cosf(a2) * 12;
        Ty = Ey + sinf(a2) * 12;
      }
      // 腕の影
      BeginMask();
      Seg(x + 3, y + 6, Ex + 3, Ey + 6, 4, 0);
      Seg(Ex + 3, Ey + 6, Tx + 3, Ty + 6, 3, 0);
      EndMask(0, 77);
      // 回る台座
      Ball(x, y, 7.2f, Flash(YELR, flash), 0.5f);
      Ring(x, y, 5.2f, PX, 0x5a4214);
      // 腕 (橙の太い腕と、それに沿う油圧の管)
      uint[] arm = Flash(ORNG, flash);
      Rod(x, y, Ex, Ey, 2.1f, arm, 0.5f);
      float ux = Ex - x, uy = Ey - y, l = Math.Max(0.1f, hypotf(ux, uy));
      Line1(x - uy / l * 1.4f + ux * 0.2f, y + ux / l * 1.4f + uy * 0.2f, x - uy / l * 1.4f + ux * 0.8f, y + ux / l * 1.4f + uy * 0.8f, 0x2a1408);
      Ball(Ex, Ey, 3.0f, STEEL, 0.6f);
      Disc(Ex, Ey, 1.0f, 0x2a2c2e);
      Rod(Ex, Ey, Tx, Ty, 1.6f, arm, 0.5f);
      // 溶接の先 (とがった口)
      float dx = Tx - Ex, dy = Ty - Ey, dl = Math.Max(0.1f, hypotf(dx, dy));
      Rod(Tx - dx / dl * 1.8f, Ty - dy / dl * 1.8f, Tx + dx / dl * 1.2f, Ty + dy / dl * 1.2f, 1.2f, GUN, 0.6f);
      Disc(Tx + dx / dl * 1.4f, Ty + dy / dl * 1.4f, 0.5f, 0x0a0a0a);
      Ep.nerve(t, 0, x, y, Ex, Ey, Tx, Ty);
      if (folded)
      {
        Plate(x - 6, y - 4.4f, 12, 3.4f, STEEL, true, 0.7f, 0.5f); // 目を覆う板
        for (int k = 0; k < 4; k++)
          Rect(x - 4.6f + k * 3, y - 3.4f, PX * 2, 1.4f, 0x4a5056);
      }
      else
        Ep.eye(x, y, 2.1f, t, flash, hurt);
      if (tipFlash) // 溶接の火
      {
        float fx = Tx + dx / dl * 1.8f, fy = Ty + dy / dl * 1.8f;
        Disc(fx, fy, 3.6f, 0xff5a3a, 70);
        Disc(fx, fy, 1.8f, 0xffd0c0);
        for (int k = 0; k < 7; k++)
        {
          float a = k * 0.9f + t, ll = 2.5f + (t + k) % 3;
          Seg(fx, fy, fx + cosf(a) * ll, fy + sinf(a) * ll, 0.45f, k % 2 != 0 ? Ep.REDW : Ep.RED);
        }
      }
    }

    static void drawFork(float x, float y, int st, int tm, int crate, int t, bool flash, bool hurt)
    {
      if (st == 0)
        y += sinf(t * 1.3f) * 0.7f; // エンジンをふかして震える
      if (st == 1) // 突進の土ぼこり
        for (int k = 0; k < 3; k++)
        {
          float r = 3 + tm / 6.0f;
          Disc(x - 8 + k * 8, y - 18 - k * 2, r, 0x968c78, 50);
          Disc(x - 8.6f + k * 8, y - 18.6f - k * 2, r * 0.6f, 0xb0a690, 40);
        }
      Rect(x - 8, y - 10, 24, 30, 0, 80);
      // タイヤ (溝が流れる)
      float[,] TIRE = { { -13.4f, -10 }, { 9.6f, -10 }, { -13.4f, 5 }, { 9.6f, 5 } };
      for (int i = 0; i < 4; i++)
      {
        float wx = x + TIRE[i, 0], wy = y + TIRE[i, 1];
        Plate(wx, wy, 3.8f, 7.4f, new uint[] { 0x050505, 0x141516, 0x24262a, 0x34383c }, true, 0.5f, 0.3f);
        float sh = (t * (st == 1 ? 0.8f : 0.15f)) % 1.5f;
        for (float k = sh; k < 7; k += 1.5f)
          Rect(wx + 0.6f, wy + k, 2.6f, PX, 0x050505);
      }
      uint[] body = Flash(YELR, flash);
      Plate(x - 10, y - 14, 20, 26, body, true, 0.66f, 0.42f);
      // 後ろの機関のふた (網)
      Plate(x - 9, y - 14, 18, 6, new uint[] { 0x1a140a, 0x3a2e18, 0x5a4a28, 0x7a6638 }, true, 0.5f, 0.3f);
      for (int k = 0; k < 6; k++)
        Rect(x - 8 + k * 3, y - 13, PX * 2, 4, 0x1a140a);
      // さび
      Disc(x + 5, y + 6, 2.2f, 0x6b3a1e);
      Disc(x + 5.4f, y + 6.3f, 1.2f, 0x8a4f28);
      Disc(x - 6, y - 4, 1.1f, 0x6b3a1e);
      Q(x + 6, y - 7, 0x8a4f28);
      // 運転席と、上の囲い (格子)
      Rect(x - 5, y - 6, 10, 9, 0x1a1b1c);
      Plate(x - 3.6f, y - 3.2f, 7.2f, 4.6f, new uint[] { 0x0a0a0a, 0x2a2420, 0x4a3e34, 0x5e5046 }, true, 0.6f, 0.4f);
      for (int s = -1; s <= 1; s += 2)
      {
        Rod(x + s * 7.5f, y - 8, x + s * 7.5f, y + 3.4f, 0.65f, GUN, 0.7f);
      }
      Rod(x - 7.5f, y - 8, x + 7.5f, y - 8, 0.65f, GUN, 0.7f);
      Rod(x - 7.5f, y + 3.4f, x + 7.5f, y + 3.4f, 0.65f, GUN, 0.7f);
      for (int k = 0; k < 3; k++)
        Line1(x - 7.4f + k * 0.2f, y - 7.6f + k * 3.6f, x + 7.4f, y - 7.6f + k * 3.6f, 0x3a3d40);
      // 前の柱と爪
      Plate(x - 7, y + 10, 14, 3, STEEL, true, 0.6f, 0.4f);
      for (int s = -1; s <= 1; s += 2)
      {
        float fx = x + s * 4.5f;
        Plate(fx - 1.5f, y + 13, 3, 12, STEEL, true, 0.7f, 0.45f);
        Tri(fx - 1.5f, y + 25, fx + 1.5f, y + 25, fx, y + 26.2f, 0x6a7178);
      }
      Ep.nerve(t, 0, x, y - 2, x - 8, y + 4, x - 6, y + 12);
      Ep.nerve(t, 7, x, y - 2, x + 7, y + 6, x + 4, y + 12);
      Ep.eye(x, y - 2, 2, t, flash, hurt);
      if (st == 0) // 排気の黒い煙
        for (int k = 0; k < 3; k++)
        {
          float p = fmod1(t * 0.08f + k / 3.0f);
          Disc(x + 7.5f + p * 3, y - 16 - p * 10, 1 + p * 3, 0x3c3c3c, (int)(153 * (1 - p)));
        }
      Rod(x + 7.2f, y - 13.6f, x + 7.2f, y - 16, 0.7f, GUN, 0.6f); // 排気の管
      if (st == 3 && crate > 0) // 爪で持ち上げた木箱
      {
        Plate(x - 11, y + 13, 22, 15, new uint[] { 0x2a1c10, 0x4a3420, 0x6a4c2c, 0x8a6a3a, 0xa8844a, 0xc49a5a }, true, 0.6f, 0.4f);
        for (int k = 0; k < 3; k++)
          Rect(x - 10.6f, y + 17 + k * 4, 21.2f, PX * 2, 0x5a4428);
        Seg(x - 10, y + 14, x + 10, y + 27, 0.8f, 0x5a4428);
        for (int k = 0; k < 4; k++) // 釘
          Q(x - 9.6f + (k % 2) * 19, y + 14 + (k / 2) * 12.4f, 0x2a1c10);
        if (crate <= 2)
        {
          Line1(x - 4, y + 13.4f, x - 1, y + 20, 0x1a1008);
          Line1(x - 1, y + 20, x + 1.5f, y + 22, 0x1a1008);
        }
      }
    }

    static void drawSnake(Enemy e, float sc, int t)
    {
      // 配管の口 (出る口と潜る口)
      for (int k = 0; k < 2; k++)
      {
        float hx = k != 0 ? e.ux : e.gx, hy = (k != 0 ? e.uy : e.gy) + sc;
        Ball(hx, hy, 8.4f, RUSTR, 0.4f);
        Disc(hx, hy, 6, 0x080808);
        Disc(hx + 0.6f, hy + 0.8f, 5, 0x000000);
        Ring(hx, hy, 6.2f, 0.4f, 0x2e170d);
        for (int b = 0; b < 8; b++) // フランジのボルト
        {
          float a = b * PIf / 4;
          Disc(hx + cosf(a) * 7.3f, hy + sinf(a) * 7.3f, 0.5f, 0x2e170d);
          Q(hx + cosf(a) * 7.3f - 0.2f, hy + sinf(a) * 7.3f - 0.2f, 0xd09a6a);
        }
      }
      for (int k = 7; k >= 0; k--)
      {
        float u = e.r - (k + 1) * SNAKE_DU, x, y;
        if (e.seg[k] == 0 || u < 0 || u > 1)
          continue;
        snakeAt(e, u, sc, out x, out y);
        Ell(x + 2, y + 3, 5.4f, 4.6f, 0, 70);
        Ball(x, y, 5.2f, e.seg[k] == 1 ? Flash(RUSTR, true) : RUSTR, 0.5f);
        Ring(x, y, 3.8f, PX * 2, 0x2e170d); // 継ぎ目
        float m = fmodf(k * 3 - t * 0.5f, 8);
        if (m < 0)
          m += 8;
        Disc(x, y, 1.0f, m < 2 ? Ep.RED : Ep.RED3);
        if (m < 2)
          Disc(x, y, 2.4f, Ep.RED, 50);
      }
      if (e.hidden)
        return;
      float hx2 = e.x, hy2 = e.y;
      Ell(hx2 + 2, hy2 + 3, 6.4f, 5.4f, 0, 77);
      Ball(hx2, hy2, 6.0f, new uint[] { 0x1e0806, 0x3a1410, 0x5a1e16, 0x7a2e22, 0x9a3a2a, 0xc0503a, 0xe8907a }, 0.5f);
      // 頭のバルブ (回るハンドル)
      Ring(hx2, hy2, 5.0f, 0.7f, 0xb0302a);
      for (int k = 0; k < 4; k++)
      {
        float a = t * 0.08f + k * PIf / 2;
        Seg(hx2, hy2, hx2 + cosf(a) * 5, hy2 + sinf(a) * 5, 0.7f, 0xb0302a);
        Q(hx2 + cosf(a) * 4.6f - 0.3f, hy2 + sinf(a) * 4.6f - 0.3f, 0xe8907a);
      }
      Ep.eye(hx2, hy2, 2, t, e.hit > 0, e.st == 1);
    }

    static void drawEye(Enemy e, int t)
    {
      float x = e.x, y = e.y, dx = cosf(e.a), dy = sinf(e.a);
      // 壁の金具
      Plate(x - 9, y - 14, 18, 7, new uint[] { 0x1e1c1a, 0x35322e, 0x45423d, 0x5a5650, 0x6b6862 }, true, 0.6f, 0.4f);
      Rect(x - 9, y - 7, 18, 2, 0, 110);
      for (int i = 0; i < 3; i++)
        Rect(x - 7 + i * 6, y - 13, 0.7f, 2 + i, 0x6b3a1e);
      foreach (float bx in new[] { x - 7.5f, x + 7.5f })
      {
        Disc(bx, y - 10.5f, 0.7f, 0x1e1c1a);
        Q(bx - 0.3f, y - 10.8f, 0x9a968e);
      }
      if (e.st <= 1) // 見張っている向き (光の扇)
      {
        int a = e.st == 1 ? 110 : 34;
        for (float r = 14; r < 90; r += 1.4f)
          foreach (float s in new[] { -0.28f, 0.28f })
            Q(x + cosf(e.a + s) * r, y + sinf(e.a + s) * r, Ep.RED, a);
        if (e.st == 1)
          for (float r = 16; r < 90; r += 6)
            for (float s = -0.24f; s <= 0.24f; s += 0.08f)
              Q(x + cosf(e.a + s) * r, y + sinf(e.a + s) * r, Ep.RED, 40);
      }
      float ox = x + dx * 12, oy = y + dy * 12;
      Seg(x, y - 7, x, y, 1.0f, 0x3a3d40);
      Ball(x, y, 2.4f, STEEL, 0.6f);
      // カメラの筒 (影と本体)
      Seg(x + 3, y + 5, ox + 3, oy + 5, 6, 0, 70);
      uint[] cam = e.hit > 0 ? Flash(WHT, true) : WHT;
      Rod(x + dx * 1.5f, y + dy * 1.5f, ox, oy, 2.9f, cam, 0.5f);
      // 日よけ
      float px = -dy, py = dx;
      Poly(0x8a867a, 255, ox + px * 3.4f, oy + py * 3.4f, ox + px * 3.4f - dx * 6, oy + py * 3.4f - dy * 6, ox + px * 2.4f - dx * 6, oy + py * 2.4f - dy * 6, ox + px * 2.4f, oy + py * 2.4f);
      // レンズ
      Disc(ox + dx * 0.6f, oy + dy * 0.6f, 2.6f, 0x141516);
      Disc(ox + dx * 0.8f, oy + dy * 0.8f, 1.8f, 0x2a2c30);
      if (e.st == 1)
      {
        Disc(ox, oy, 4.2f, Ep.RED, 56);
        Disc(ox + dx * 0.8f, oy + dy * 0.8f, 1.2f, ((t >> 1) & 1) != 0 ? Ep.RED : Ep.RED2);
      }
      else
        Disc(ox + dx * 0.8f, oy + dy * 0.8f, 0.9f, 0x7a1a1a);
      Q(ox + dx * 0.8f - 0.6f, oy + dy * 0.8f - 0.6f, 0xffffff);
      // 録画の灯
      if (((t >> 4) & 1) != 0)
        Disc(x - dx * 0.5f + px * 2, y - dy * 0.5f + py * 2, 0.5f, Ep.RED);
      Ep.nerve(t, 0, x, y - 14, x + 2, y - 10, x, y - 7);
    }

    static readonly uint[] SCOLS = { 0x6b3a1e, 0x8a4f28, 0x7d8388, 0x4a2a1a, 0xa8683a, 0x55585b };
    static void drawScrap(Enemy e, int t)
    {
      float x = e.x, y = e.y, r = e.r;
      Ell(x + 3, y + 4, r, r * 0.9f, 0, 85);
      // 中の赤い核 (すきまから見える)
      Disc(x, y, r * 0.7f, 0x1a0806);
      Disc(x, y, r * 0.5f, Ep.RED3);
      Disc(x, y, r * 0.35f, e.hit > 0 ? 0xffffffu : Ep.RED);
      Disc(x, y, r * 0.75f, Ep.RED, 30);
      // くっついた鉄くず (板・管・歯車)
      int n = (int)(14 + r * 2.4f);
      for (int i = 0; i < n; i++)
      {
        float an = i * 2.39f + e.a, d = r * (0.3f + 0.7f * hs(i)), px = x + cosf(an) * d, py = y + sinf(an) * d, s = 2 + hs(i * 3) * 4;
        uint c = SCOLS[i % 6];
        float ra = an + hs(i * 7) * 3;
        float ca = cosf(ra), sa = sinf(ra);
        if (i % 5 == 0) // 管
          Rod(px - ca * s, py - sa * s, px + ca * s, py + sa * s, 0.8f, i % 2 == 0 ? RUSTR : DSTEEL, 0.4f);
        else if (i % 7 == 3) // 歯車
        {
          Disc(px, py, s * 0.55f, c);
          for (int k = 0; k < 6; k++)
            Q(px + cosf(k + ra) * s * 0.7f, py + sinf(k + ra) * s * 0.7f, c);
          Disc(px, py, s * 0.2f, 0x1a1a1a);
        }
        else // 板
        {
          float w = s * 0.5f, h = s * 0.3f;
          Poly(Mul(c, 0.6f), 255, px - ca * w - sa * h - 0.3f, py - sa * w + ca * h + 0.3f, px + ca * w - sa * h + 0.3f, py + sa * w + ca * h + 0.3f, px + ca * w + sa * h + 0.3f, py + sa * w - ca * h + 0.3f, px - ca * w + sa * h - 0.3f, py - sa * w - ca * h - 0.3f);
          Poly(c, 255, px - ca * w - sa * h, py - sa * w + ca * h, px + ca * w - sa * h, py + sa * w + ca * h, px + ca * w + sa * h, py + sa * w - ca * h, px - ca * w + sa * h, py - sa * w - ca * h);
          Q(px - ca * w * 0.6f + sa * h * 0.6f, py - sa * w * 0.6f - ca * h * 0.6f, Mix(c, 0xffffff, 0.45f));
        }
      }
      for (int i = 0; i < 8; i++) // 吸い寄せられてくる鉄くず
      {
        float ph = fmod1(t * 0.01f + hs(i + 40)), dist = 40 * (1 - ph) + r, ang = hs(i + 50) * 6.28f;
        float px = x + cosf(ang) * dist, py = y + sinf(ang) * dist;
        Rect(px - 0.8f, py - 0.8f, 1.6f, 1.6f, i % 2 != 0 ? 0x8a4f28u : 0x7d8388u);
        Seg(px, py, px + cosf(ang) * 2.5f, py + sinf(ang) * 2.5f, 0.4f, 0xffffff, 40);
      }
    }

    static void drawMaint(Enemy e, Enemy tg, int t)
    {
      float x = e.x, y = e.y;
      if (tg != null && e.st == 0 && hypotf(tg.x + 14 - x, tg.y - 16 - y) < 12) // 直している光
      {
        int n = 24;
        for (int k = 0; k < n; k++)
          if ((k + (t >> 1)) % 4 < 2)
          {
            float px = x + (tg.x - x) * k / n, py = y + 3 + (tg.y - y - 3) * k / n;
            Rect(px - 0.35f, py - 0.35f, 0.7f, 0.7f, Ep.RED);
          }
        Disc(tg.x, tg.y, 5, Ep.RED, 30);
        if (e.cnt > 0)
          for (int k = 0; k < 3; k++) // 直った印 (+)
          {
            float px = tg.x - 6 + k * 6, py = tg.y - 6 - (10 - e.cnt) * 1.4f;
            Rect(px - 1.2f, py - 0.3f, 2.4f, 0.7f, Ep.RED);
            Rect(px - 0.35f, py - 1.2f, 0.7f, 2.4f, Ep.RED);
          }
      }
      Ell(x + 6, y + 12, 5, 3.6f, 0, 70);
      int[,] ARM = { { -6, -6 }, { 6, -6 }, { -6, 6 }, { 6, 6 } };
      for (int i = 0; i < 4; i++)
      {
        float ax = x + ARM[i, 0], ay = y + ARM[i, 1];
        Seg(x, y, ax, ay, 0.8f, 0x55585b);
        Ring(ax, ay, 3.0f, 0.6f, 0x55585b);
        rotor(ax, ay, 2.7f, t * 1.1f * (i % 2 == 0 ? 1 : -1) + i, false, 0xd0d4d8);
        Disc(ax, ay, 0.6f, 0x2a2c2e);
      }
      Ball(x, y, 4.3f, e.hit > 0 ? Flash(WHT, true) : WHT, 0.7f);
      // 黄色と黒の帯、赤十字
      Rect(x - 2.6f, y + 2.6f, 5.2f, 1.6f, 0x1a1a1a);
      for (float k = 0; k < 5; k += 1.3f)
        Rect(x - 2.6f + k, y + 2.6f, 0.65f, 1.6f, 0xc9a227);
      Rect(x - 0.35f, y - 3.4f, 0.7f, 2.2f, 0xd02a1a);
      Rect(x - 1.1f, y - 2.65f, 2.2f, 0.7f, 0xd02a1a);
      Ep.eye(x, y + 0.6f, 1.1f, t, false, e.st == 1);
    }

    static void drawCore(Enemy e)
    {
      float x = e.x, y = e.y;
      for (int k = 1; k < 12; k++) // 光の尾
        Disc(x - e.vx * k * 1.3f, y - e.vy * k * 1.3f, 1.5f * (1 - k / 12f) + 0.3f, Ep.RED, (int)(180 * (1 - k / 12.0f)));
      Disc(x, y, 5.5f, Ep.RED, 22);
      Disc(x, y, 3.8f, Ep.RED, 50);
      Ball(x, y, 2.9f, new uint[] { 0x5a0c08, 0x8a160e, Ep.RED2, Ep.RED, 0xff7a6a, 0xffd0c8 }, 0.9f, false, 0.4f);
      Q(x - 1.0f, y - 1.0f, Ep.REDW);
    }

    // 強くなった (核が乗り移った) しるし: 脈打つ赤い輪と、段の数の点
    static void lvlMark(float x, float y, float rad, int lvl, int t)
    {
      Ring(x, y, rad + sinf(t * 0.2f), 0.6f, Ep.RED, 90 + (int)(50 * sinf(t * 0.2f)));
      for (int i = 0; i < lvl; i++)
      {
        float px = x - lvl * 2 + i * 4 + 1, py = y - rad - 3;
        Poly(Ep.RED, 255, px, py - 1.2f, px + 1.2f, py, px, py + 1.2f, px - 1.2f, py);
      }
    }

    static void drawEShot(int k, float x, float y, float dx, float dy, int life, int t)
    {
      float l = hypotf(dx, dy);
      if (l == 0)
        l = 1;
      float ux = dx / l, uy = dy / l, nx = -uy, ny = ux;
      if (k == 1) // 溶接の火花 (ジグザグ)
      {
        float j = ((t & 1) != 0 ? 1 : -1) * 1.5f;
        Disc(x, y, 3.2f, 0xff3c28, 40);
        Seg(x, y, x + ux * 4 + nx * j, y + uy * 4 + ny * j, 0.8f, Ep.RED);
        Seg(x + ux * 4 + nx * j, y + uy * 4 + ny * j, x + ux * 7, y + uy * 7, 0.8f, Ep.RED);
        Line1(x, y, x + ux * 4 + nx * j, y + uy * 4 + ny * j, 0xffd0c0);
        Disc(x, y, 0.9f, Ep.REDW);
      }
      else if (k == 2) // 橙の大きな弾 (ボス)
      {
        Disc(x, y, 6.4f, 0xff7828, 50);
        Disc(x, y, 4.6f, 0xff7828, 90);
        Ball(x, y, 3.8f, new uint[] { 0x8a2a08, 0xc2410c, 0xff7a2a, 0xffa050, 0xffd090, 0xfff6e0 }, 0.8f, false, 0.45f);
      }
      else if (k == 3) // 蒸気
      {
        float q = 1 - life / 18.0f;
        Disc(x, y, 2 + q * 5, 0xd2dce1, (int)(110 * (1 - q)) + 25);
        Disc(x - 0.8f, y - 0.8f, (2 + q * 5) * 0.55f, 0xf0f4f6, (int)(90 * (1 - q)) + 20);
      }
      else // 赤いひし形
      {
        Disc(x, y, 3.4f, 0xff3c28, 50);
        Poly(0x8a160e, 255, x + ux * 4.5f, y + uy * 4.5f, x + nx * 2.2f, y + ny * 2.2f, x - ux * 3.5f, y - uy * 3.5f, x - nx * 2.2f, y - ny * 2.2f);
        Poly(Ep.RED, 255, x + ux * 3.8f, y + uy * 3.8f, x + nx * 1.6f, y + ny * 1.6f, x - ux * 2.8f, y - uy * 2.8f, x - nx * 1.6f, y - ny * 1.6f);
        Poly(0xffb0a0, 255, x + ux * 2.6f, y + uy * 2.6f, x + nx * 0.6f, y + ny * 0.6f, x - ux * 0.6f, y - uy * 0.6f, x - nx * 0.6f, y - ny * 0.6f);
      }
    }

    static readonly float[] LRAD = { 16, 15, 7, 12, 18, 9, 10, 0, 10, 0 };

    // air: false なら床の敵 (戦車より下に描く)、true なら空の敵と壁のカメラ (手前の絵より上に描く)
    void drawEnemies(bool air)
    {
      float sc = SC;
      foreach (Enemy e in en)
      {
        bool up = isAir(e.k) || e.k == EYE;
        if (up != air)
          continue;
        if (e.k != SNAKE && (e.y < -40 || e.y > H + 40))
          continue;
        int age = t - e.t0;
        bool flash = e.hit > 0, hurt = weak(e) && e.k != BUG && e.k != CORE;
        switch (e.k)
        {
          case TWIN:
            drawTwin(e.x, e.y, t, flash, hurt, e.st == 1);
            break;
          case WALKER:
            drawWalker(e.x, e.y, t, (e.st == 1 || age % 120 < 70) ? t : 0, flash, hurt);
            break;
          case BUG:
            drawBug(e.x, e.y, e.a, t, e.id, flash);
            break;
          case WELDER:
            drawWelder(e.x, e.y, e.a, e.a + 0.5f * sinf(age * 0.05f + 1), e.st == 1, e.st == 0 && e.tm > 0, t, flash, hurt);
            break;
          case FORK:
            drawFork(e.x, e.y, e.st, e.tm, e.cnt, t, flash, hurt);
            break;
          case SNAKE:
            drawSnake(e, sc, t);
            break;
          case EYE:
            drawEye(e, t);
            break;
          case SCRAP:
            drawScrap(e, t);
            break;
          case MAINT:
            drawMaint(e, findId(e.link), t);
            break;
          case CORE:
            drawCore(e);
            break;
        }
        if (hurt && !e.hidden && e.virus == 0)
          Ep.sparks(e.x, e.y, t, 4);
        if (e.virus > 0 && (e.virus > 60 || (t & 4) != 0)) // ウイルス: 青い輪と、ちらつく青いノイズ (終わりぎわは点滅)
        {
          float rad = e.k == SCRAP ? e.r + 3 : e.k == BUG ? 7 : 13;
          Ring(e.x, e.y, rad, 0.6f, 0x3a7bff, 150);
          Ring(e.x, e.y, rad + 1.2f, PX, 0x6ab8ff, 80);
          for (int i = 0; i < 9; i++)
            Rect(e.x + (hs(t * 1.3f + i + e.id) - 0.5f) * rad * 2, e.y + (hs(t * 2.1f + i * 3 + e.id) - 0.5f) * rad * 2, 0.6f + (i & 1) * 2, 0.4f,
              i % 3 != 0 ? 0x6ab8ffu : 0xe0f0ffu);
          // 小さな青い 0/1 の文字の代わりに点の並び
          for (int i = 0; i < 4; i++)
            Rect(e.x - 2 + i * 1.3f, e.y - rad - 4, 0.7f, ((t / 3 + i) & 1) != 0 ? 1.6f : 0.7f, 0x6ab8ff);
        }
        if (e.lvl > 0 && !e.hidden)
          lvlMark(e.x, e.y, e.k == SCRAP ? e.r + 4 : LRAD[e.k], e.lvl, t);
      }
    }
  }
}
