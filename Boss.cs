// ボス 3 体。動きは ESP32 版 (game2_boss.cpp) と同じ。絵は 3 倍の細かさで描き直したもの。
// 区画の決まった所で出て、出ているあいだは流れが止まる (spd)。上から降りてきて (enterT)、倒すと大きな爆発 (dieT)。
//   0 ガントリー: 沈殿槽の天井クレーン。台車が左右に動き、影の所へフックを落とす。弱ると桁の端の制御盤へ逃げて直す
//   1 スタンパー: プレス機が油圧の脚で歩く。ヘッドを落として衝撃波の輪。ヘッドが上がると下の核が撃てる。脚を全部壊すと這って逃げる
//   2 炉心: 溶鉱炉。周りの羽口 8 つが順に橙の大きな弾を撃つ。追い詰めると、核を呼び集めて直そうとする
using System;

namespace Foundry
{
  public partial class Game
  {
    public class Boss
    {
      public int k = -1;                     // -1 はいない
      public int t, st, tm, stun;            // stun: 爆弾のウイルスで止まっている残り
      public int enterT, dieT, heals, fireIdx;
      public float x, y, u, elev, ty;
      public int[] hp = new int[9], maxhp = new int[9], hit = new int[9]; // 部位ごとの耐久 (0 がいちばん大事な部位)
      public float hookY, hookOff;
      public float ringX, ringY, ringR;
      public bool ringOn, escaped;
    }
    Boss boss = new Boss();

    const int ENTER = 90;
    static readonly string[] BOSS_NAME = { "GANTRY", "STAMPER", "HEARTH" };
    static readonly int[] BONUS = { 10000, 15000, 30000 };

    static string bossName(int k) { return k >= 0 && k < 3 ? BOSS_NAME[k] : ""; }

    // スタンパーの脚 k (0〜3) の付け根・ひざ・足
    static readonly int[] SLX = { -1, 1, -1, 1 }, SLY = { -1, -1, 1, 1 };
    static void stampLeg(Boss B, int k, int t, out float hx, out float hy, out float kx, out float ky, out float fx, out float fy)
    {
      float sx = SLX[k], sy = SLY[k];
      float step = B.st == 0 && B.stun == 0 ? sinf(t * 0.06f + (k == 0 || k == 3 ? 0 : PIf)) * 5 : 0;
      hx = B.x + sx * 32;
      hy = B.y + sy * 14;
      fx = hx + sx * 16;
      fy = hy + sy * 12 + step;
      kx = hx + sx * 12;
      ky = hy - 10 + sy * 2;
    }

    // 炉心の羽口 k (0〜7) の位置と向き
    static void hearthTur(Boss B, int k, float tx, float ty, out float bx, out float by, out float aim)
    {
      float a = k / 8.0f * PIf * 2 + 0.2f;
      bx = B.x + cosf(a) * 52;
      by = B.y + sinf(a) * 52;
      aim = a + clampf(angDiff(atan2f(ty - by, tx - bx), a), -0.9f, 0.9f);
    }

    float bossEyeX() { return boss.x + 5 * sinf(boss.t * 0.023f); }
    float bossEyeY() { return boss.y + 3 + 3 * sinf(boss.t * 0.031f); }

    void startBoss(int k)
    {
      boss = new Boss();
      boss.k = k;
      boss.enterT = ENTER;
      boss.x = W / 2;
      boss.y = -90;
      bossBannerT = 3 * FPS;
      bossResult = 0;
      sfx(Sfx.Warning);
      float m = 1 + 0.5f * Math.Min(lap, 2); // 2 周目からは硬い
      Action<int, int> set = (i, hp) => { boss.hp[i] = boss.maxhp[i] = (int)(hp * m); };
      if (k == 0)
      {
        boss.ty = 56;
        set(0, 120);
        set(1, 30);
        set(2, 30);
      }
      else if (k == 1)
      {
        boss.ty = 92;
        set(0, 70);
        for (int i = 1; i <= 4; i++)
          set(i, 32);
      }
      else
      {
        boss.ty = 80;
        set(0, 150);
        for (int i = 1; i <= 8; i++)
          set(i, 20);
      }
    }

    // 部位の位置 (爆発と爆弾に使う)
    static void partPos(Boss B, int part, int t, float tx, float ty, out float x, out float y)
    {
      x = B.x;
      y = B.y;
      if (B.k == 0)
      {
        if (part == 1 || part == 2)
        {
          x = part == 1 ? 26 : W - 26;
          y = B.y + 7;
        }
      }
      else if (B.k == 1)
      {
        if (part == 0)
          y = B.y + 2;
        else
        {
          float hx, hy, kx, ky;
          stampLeg(B, part - 1, t, out hx, out hy, out kx, out ky, out x, out y);
          x = kx;
          y = ky;
        }
      }
      else if (part > 0)
      {
        float a;
        hearthTur(B, part - 1, tx, ty, out x, out y, out a);
      }
    }

    void bossHurt(int part, int dmg)
    {
      Boss B = boss;
      if (B.k < 0 || B.dieT > 0 || B.enterT > 0 || B.hp[part] <= 0)
        return;
      B.hp[part] -= dmg;
      B.hit[part] = 3;
      if (B.hp[part] > 0)
      {
        sfx(Sfx.MomHit, 0.3f);
        return;
      }
      B.hp[part] = 0;
      float x, y;
      partPos(B, part, t, tx, ty, out x, out y);
      fx.Add(new Fx(0, x, y, hs(t) * 6, 0, 26, false));
      if (part > 0)
      {
        sfx(Sfx.BigBoom, 0.7f);
        if (mode == Mode.Play)
          Score += 1000;
        return;
      }
      // 倒した
      B.dieT = 1;
      sfx(Sfx.Crash);
      bossResult = 1;
      bossBannerT = 4 * FPS;
      if (mode == Mode.Play)
        Score += BONUS[B.k] * (1 + Math.Min(lap, 2));
      dropItem(B.x - 20, B.y);
      dropItem(B.x + 20, B.y);
      eb.Clear();
      foreach (Enemy e in en)
        if (e.k == CORE && e.link == -2) // 呼び集めていた核も消える
          e.gone = true;
    }

    void bossShoot(float x, float y, float a, float s, int k)
    {
      if (eb.Count < 30)
      {
        eb.Add(new Shot(x, y, cosf(a) * s, sinf(a) * s, k, 0));
        sfx(k == 2 ? Sfx.Flak : Sfx.EnemyShot, 0.5f);
      }
    }

    void bossStep()
    {
      Boss B = boss;
      if (B.k < 0)
        return;
      B.t++;
      for (int i = 0; i < 9; i++)
        if (B.hit[i] > 0)
          B.hit[i]--;
      if (bossBannerT > 0)
        bossBannerT--;

      if (B.dieT > 0) // 大きな爆発が続いて、消える
      {
        int len = B.k == 2 ? 120 : 90;
        if (B.dieT % 5 == 1)
        {
          float r = B.k == 0 ? 110 : 45;
          fx.Add(new Fx(0, B.x + (hs(t) - 0.5f) * r * 2, B.y + (hs(t * 1.7f) - 0.5f) * (B.k == 0 ? 30 : r * 1.4f), hs(t + 2) * 6, 0, 26, false));
        }
        if (++B.dieT > len)
        {
          fx.Add(new Fx(2, B.x, B.y, hs(t) * 6, 0, 40, false));
          B.k = -1;
        }
        return;
      }
      if (B.enterT > 0) // 上から降りてくる
      {
        B.enterT--;
        B.y = B.ty - (float)B.enterT / ENTER * (B.ty + 90);
        return;
      }

      bool hittable = mode == Mode.Play && tankOn && invT == 0 && !autoPilot;
      bool canShoot = tankOn && enterT == 0;
      float bs = 1.6f + 0.3f * Math.Min(lap, 2); // 弾の速さ
      Func<int, int> per = b => lap > 0 ? b * 3 / 4 : b;

      // 衝撃波の輪 (止まっていても広がる)
      if (B.ringOn)
      {
        B.ringR += 3;
        if (hittable && fabsf(hypotf(tx - B.ringX, ty - B.ringY) - B.ringR) < 5)
          killTank();
        if (B.ringR > 85)
          B.ringOn = false;
      }
      if (B.stun > 0) // ウイルスで止まっている
      {
        B.stun--;
        return;
      }

      if (B.k == 0) // ---- ガントリー ----
      {
        float gy = B.y;
        if (B.st == 0)
        {
          int c = B.tm % 110;
          B.tm++;
          if (c < 40 || c >= 90)
          {
            B.u += 0.018f;
            B.x = W / 2 + 84 * sinf(B.u);
          }
          if (c == 0)
            B.hookY = clampf(ty, 150, H - 30); // 戦車のいる高さを狙う
          float full = B.hookY - (gy + 18);
          B.hookOff = c < 40 ? 0 : c < 50 ? (c - 40) / 10.0f * full : c < 80 ? full : c < 100 ? full * (1 - (c - 80) / 20.0f) : 0;
          B.elev = c; // 描くとき用 (影とフックの段階)
          if (c == 50)
          {
            fx.Add(new Fx(5, B.x, B.hookY, hs(t) * 6, 0, 16, false));
            sfx(Sfx.Slam);
            if (hittable && hypotf(tx - B.x, ty - B.hookY) < 16)
              killTank();
          }
          if (c == 20 && canShoot) // 台車から 5 方向 (1 回りに 1 度)
          {
            float a0 = clampf(atan2f(ty - gy - 14, tx - B.x), 0.4f, PIf - 0.4f);
            foreach (float d in new[] { -0.5f, -0.25f, 0.0f, 0.25f, 0.5f })
              bossShoot(B.x, gy + 14, a0 + d, bs, 0);
          }
          for (int i = 1; i <= 2; i++) // 桁の目は橙の大きな弾
            if (B.hp[i] > 0 && canShoot && (B.t + i * 60) % per(120) == 0)
            {
              float ex = i == 1 ? 26 : W - 26, ey = gy + 7;
              bossShoot(ex, ey, atan2f(ty - ey, tx - ex), bs * 0.85f, 2);
            }
          if (B.hp[0] * 10 < B.maxhp[0] * 3 && B.heals < 2) // 弱ったので、制御盤へ逃げ込む
          {
            B.st = 1;
            B.hookOff = 0;
            B.elev = 100;
          }
        }
        else if (B.st == 1)
        {
          float gx = B.x < W / 2 ? 20 : W - 20;
          B.x += clampf(gx - B.x, -2.2f, 2.2f);
          if (fabsf(gx - B.x) < 0.5f)
          {
            B.st = 2;
            B.tm = 0;
          }
        }
        else // 2: 制御盤で直している
        {
          if (++B.tm % 3 == 0)
            B.hp[0] = Math.Min(B.maxhp[0], B.hp[0] + 1);
          if (B.tm >= 75)
          {
            B.heals++;
            B.st = 0;
            B.tm = 0;
            B.u = asinf(clampf((B.x - W / 2) / 84.0f, -1, 1));
          }
        }
      }
      else if (B.k == 1) // ---- スタンパー ----
      {
        int legs = 0;
        for (int i = 1; i <= 4; i++)
          if (B.hp[i] > 0)
            legs++;
        if (B.st == 0)
        {
          if (legs == 0)
            B.st = 1;
          B.u += 0.01f * (0.4f + 0.6f * legs / 4);
          B.x = W / 2 + 50 * sinf(B.u);
          B.y = B.ty + 10 * sinf(B.u * 1.3f);
        }
        else // 脚がない: 這って上へ逃げる
        {
          B.y -= 0.35f;
          B.x += (W / 2 - B.x) * 0.01f;
          if (B.y < -70)
          {
            B.k = -1;
            B.escaped = true;
            bossResult = 2;
            bossBannerT = 3 * FPS;
            return;
          }
        }
        int c = B.tm % 120;
        B.tm++;
        B.elev = c < 60 ? c / 60.0f * 26 : c < 68 ? 26 * (1 - (c - 60) / 8.0f) : 0;
        if (c == 68) // ヘッドを落とす: 衝撃波
        {
          B.ringOn = true;
          sfx(Sfx.Slam);
          B.ringR = 8;
          B.ringX = B.x;
          B.ringY = B.y + 6;
          if (legs <= 2 && canShoot)
            for (int k = 0; k < 8; k++)
              bossShoot(B.x, B.y + 6, k * PIf / 4 + 0.2f, bs * 0.8f, 0);
        }
        if (canShoot && B.t % per(80) == 0) // 柱から狙う
        {
          float px = B.x + (B.fireIdx++ % 2 != 0 ? 32 : -32), py = B.y - 18;
          bossShoot(px, py, atan2f(ty - py, tx - px), bs, 0);
        }
        if (hittable)
        {
          bool hit = fabsf(tx - B.x) < 34 && fabsf(ty - B.y) < 22;
          for (int k = 0; k < 4 && !hit; k++)
            if (B.hp[k + 1] > 0)
            {
              float hx, hy, kx, ky, fx_, fy_;
              stampLeg(B, k, t, out hx, out hy, out kx, out ky, out fx_, out fy_);
              hit = hypotf(tx - fx_, ty - fy_) < 9;
            }
          if (hit)
            killTank();
        }
      }
      else // ---- 炉心 ----
      {
        B.x = W / 2;
        if (canShoot && B.t % per(26) == 0) // 生きている羽口が順に撃つ
          for (int n = 0; n < 8; n++)
          {
            int k = (B.fireIdx++) % 8;
            if (B.hp[k + 1] <= 0)
              continue;
            float bx, by, aim;
            hearthTur(B, k, tx, ty, out bx, out by, out aim);
            bossShoot(bx + cosf(aim) * 8, by + sinf(aim) * 8, aim, bs * 0.95f, 2);
            break;
          }
        if (B.st == 0 && ((B.heals == 0 && B.hp[0] * 2 < B.maxhp[0]) || (B.heals == 1 && B.hp[0] * 5 < B.maxhp[0])))
        {
          B.st = 1; // 追い詰められたので、工場じゅうの核を呼び集める
          B.tm = 0;
          B.heals++;
        }
        if (B.st == 1)
        {
          B.tm++;
          if (B.tm % 20 == 0 && B.tm <= 160)
          {
            int side = (B.tm / 20) % 4;
            float x = side == 0 ? -8 : side == 1 ? W + 8 : side == 2 ? 30 + hs(t) * 60 : W - 30 - hs(t) * 60;
            float y = side < 2 ? 150 + hs(t + 1) * 120 : H + 8;
            Enemy c = addEnemy(CORE, x, y);
            c.link = -2; // 炉心の目へ向かう
            sfx(Sfx.CoreFree);
            c.lvl = 0;
          }
          if (B.tm > 220)
            B.st = 0;
        }
        if (hittable && hypotf(tx - B.x, ty - B.y) < 50)
          killTank();
      }
    }

    // 自分の弾が当たったか。当たったら傷を負わせて true (硬い所に当たったら、はじいて true)
    bool bossShot(float x, float y)
    {
      Boss B = boss;
      if (B.k < 0 || B.dieT > 0)
        return false;
      Action spark = () =>
      {
        fx.Add(new Fx(3, x, y, hs(t) * 6, 0, 5, false));
        sfx(Sfx.Ping);
      };
      if (B.enterT > 0)
        return false;
      if (B.k == 0)
      {
        float gy = B.y;
        if (fabsf(x - B.x) < 14 && y > gy - 16 && y < gy + 16)
        {
          bossHurt(0, 1);
          return true;
        }
        for (int i = 1; i <= 2; i++)
          if (B.hp[i] > 0 && hypotf(x - (i == 1 ? 26 : W - 26), y - (gy + 7)) < 9)
          {
            bossHurt(i, 1);
            return true;
          }
        return false;
      }
      if (B.k == 1)
      {
        for (int k = 0; k < 4; k++)
          if (B.hp[k + 1] > 0)
          {
            float hx, hy, kx, ky, fx_, fy_;
            stampLeg(B, k, t, out hx, out hy, out kx, out ky, out fx_, out fy_);
            if (hypotf(x - kx, y - ky) < 7 || hypotf(x - fx_, y - fy_) < 7 || hypotf(x - (hx + kx) / 2, y - (hy + ky) / 2) < 6)
            {
              bossHurt(k + 1, 1);
              return true;
            }
          }
        if (B.elev > 14 && hypotf(x - B.x, y - (B.y + 2)) < 9) // ヘッドが上がると核が見える
        {
          bossHurt(0, 1);
          return true;
        }
        // ヘッドが下りているときは、ヘッドが核を覆っていて弾をはじく。上がっているときは弾が体の上を越えて核に届く
        float hy2 = B.y - 20 - B.elev;
        if (B.elev <= 14 && fabsf(x - B.x) < 32 && y > hy2 - 14 && y < hy2 + 28)
        {
          spark();
          return true;
        }
        return false;
      }
      for (int k = 0; k < 8; k++)
        if (B.hp[k + 1] > 0)
        {
          float bx, by, a;
          hearthTur(B, k, tx, ty, out bx, out by, out a);
          if (hypotf(x - bx, y - by) < 7)
          {
            bossHurt(k + 1, 1);
            return true;
          }
        }
      // 弾は炉の上を越えて飛ぶ。真ん中の目に届いたら当たる (まばたきしているあいだは、はじく)
      if (hypotf(x - bossEyeX(), y - bossEyeY()) < 11)
      {
        if ((B.t % 140) < 6)
          spark();
        else
          bossHurt(0, 1);
        return true;
      }
      return false;
    }

    // 爆弾: 近くの部位に 2 発ぶん。本体の近くならウイルスで 3 秒止める
    void bossBomb(float x, float y)
    {
      Boss B = boss;
      if (B.k < 0 || B.dieT > 0 || B.enterT > 0)
        return;
      int n = B.k == 0 ? 3 : B.k == 1 ? 5 : 9;
      for (int i = 0; i < n; i++)
      {
        if (B.hp[i] <= 0)
          continue;
        if (i == 0 && B.k == 1 && B.elev <= 14) // スタンパーの核はヘッドの下
          continue;
        float px, py;
        partPos(B, i, t, tx, ty, out px, out py);
        if (hypotf(px - x, py - y) < 42)
          bossHurt(i, 2);
      }
      if (B.k >= 0 && B.dieT == 0)
      {
        float d = B.k == 0 ? fabsf(y - B.y) : hypotf(x - B.x, y - B.y);
        if (d < (B.k == 0 ? 40 : 70))
          B.stun = 3 * FPS;
      }
    }

    // ================= ボスの絵 =================
    static readonly uint[] GIRDER = { 0x0e2018, 0x1d3f31, 0x24473a, 0x2f6b52, 0x3f8a6a, 0x5aa684, 0x8ccab0 };
    static readonly uint[] PRESS = { 0x1a1410, 0x2e241c, 0x44382c, 0x55402f, 0x6e5644, 0x8a7060, 0xa89080 };
    static readonly uint[] BRICK = { 0x1a0c06, 0x2e170d, 0x3a2216, 0x5a3422, 0x7a4a30, 0x8a5a3a, 0xa8784e };

    static void stunNoise(float x, float y, float rx, float ry, int t)
    {
      for (int i = 0; i < 22; i++)
        Rect(x + (hs(t * 1.3f + i) - 0.5f) * rx * 2, y + (hs(t * 2.1f + i * 3) - 0.5f) * ry * 2, 0.7f + (i & 1) * 3, 0.5f, i % 3 != 0 ? 0x6ab8ffu : 0xe0f0ffu);
    }

    // 桁の格子 (緑のトラス)
    static void girder(float x0, float x1, float gy, float h, int t)
    {
      float w = x1 - x0;
      Plate(x0, gy, w, h, GIRDER, false, 0.45f, 0.3f);
      Rect(x0, gy, w, 1.2f, 0x5aa684);
      Rect(x0, gy, w, PX, 0x8ccab0);
      Rect(x0, gy + h - 1.2f, w, 1.2f, 0x1d3f31);
      Rect(x0, gy + 2, w, h - 4, 0x14302a);
      for (float i = x0; i < x1; i += 10)
      {
        Seg(i, gy + 2, i + 5, gy + h - 2, 1.1f, 0x3f8a6a);
        Seg(i + 5, gy + h - 2, i + 10, gy + 2, 1.1f, 0x2f6b52);
        Line1(i + 0.3f, gy + 2, i + 5.3f, gy + h - 2.4f, 0x8ccab0);
        Disc(i, gy + 1.2f, 0.55f, 0x0e2018); // リベット
        Q(i - 0.3f, gy + 0.9f, 0xb8e0d0);
        Disc(i + 5, gy + h - 1.2f, 0.55f, 0x0e2018);
      }
    }

    static void drawGantry(Boss B, int t)
    {
      float gy = B.y, x = B.x;
      int c = (int)B.elev;
      // フックの落ちる所の影と、落ちたときの輪
      if (B.st == 0 && c < 50)
      {
        float r = 2 + Math.Min(1.0f, c / 40.0f) * 9;
        Ell(x, B.hookY, r * 1.1f, r * 0.8f, 0, 40 + c * 2);
        if (c > 30 && (t & 2) != 0) // 狙いの印
          Ring(x, B.hookY, r + 2, 0.4f, Ep.RED, 120);
      }
      if (B.st == 0 && c >= 50 && c < 64)
      {
        float q = (c - 50) / 14.0f;
        Ring(x, B.hookY, 4 + q * 16, 1.0f, 0xc8f0eb, (int)(180 * (1 - q)));
        Ring(x, B.hookY, 2 + q * 9, 0.8f, 0xc8f0eb, (int)(130 * (1 - q)));
      }
      Rect(0, gy + 26, W, 13, 0, 70);
      girder(0, W, gy, 16, t);
      // 両端の制御盤
      block(0, gy - 4, 12, 24, 6, 0x3f8a6a, 0x24473a);
      block(W - 12, gy - 4, 12, 24, 6, 0x3f8a6a, 0x24473a);
      for (int s = 0; s < 2; s++) // 制御盤のランプ (直しているあいだは速く点滅)
      {
        float px = s != 0 ? W - 8 : 4;
        bool fast = B.st == 2 && (x < W / 2) == (s == 0);
        bool g = fast ? ((t >> 1) & 1) != 0 : ((t >> 4) & 1) != 0, r = fast ? ((t >> 1) & 1) != 0 : ((t >> 5) & 1) != 0;
        Disc(px + 0.5f, gy - 5.5f, 0.8f, g ? 0x7cff8au : 0x1d3f31u);
        Disc(px + 3.5f, gy - 5.5f, 0.8f, r ? Ep.RED : 0x3a1410u);
        if (g)
          Disc(px + 0.5f, gy - 5.5f, 2, 0x7cff8a, 50);
        for (int k = 0; k < 3; k++) // 計器の窓
          Rect(px - 1 + k * 2.4f, gy - 2.6f, 1.6f, 1.0f, 0x0a1a14);
      }
      Ep.nerve(t, 0, 0, gy + 14, W, gy + 14);
      Ep.nerve(t, 6, 0, gy + 1, W, gy + 1);
      for (int i = 1; i <= 2; i++)
      {
        float ex = i == 1 ? 26 : W - 26;
        Ball(ex, gy + 7, 5.0f, GIRDER, 0.4f);
        if (B.hp[i] > 0)
          Ep.eye(ex, gy + 7, 3, t, B.hit[i] > 0, B.hp[i] * 3 <= B.maxhp[i]);
        else
        {
          Disc(ex, gy + 7, 4, 0x141516);
          Ep.sparks(ex, gy + 7, t, 2);
        }
      }
      // ワイヤーとフック
      Seg(x - 0.7f, gy + 12, x - 0.7f, gy + 16 + B.hookOff, 0.5f, 0x2a2b2c);
      Seg(x + 0.7f, gy + 12, x + 0.7f, gy + 16 + B.hookOff, 0.5f, 0x4a4b4c);
      float hy = gy + 18 + B.hookOff;
      Ell(x + 2, hy + 3.5f, 4, 2.6f, 0, 70);
      Plate(x - 2.6f, hy - 4.6f, 5.2f, 3.2f, YELR, true, 0.7f, 0.5f); // 滑車の箱
      Rod(x, hy - 1.4f, x, hy + 1, 0.7f, STEEL, 0.6f);
      Ring(x - 1.2f, hy + 2.4f, 2.4f, 1.0f, 0x6a7178); // かぎ
      Ring(x - 1.4f, hy + 2.2f, 2.4f, PX, 0xc4cacf);
      // 台車
      block(x - 13, gy - 6, 26, 22, 8, B.hit[0] > 0 ? 0xf0d878u : 0xc9a227u, 0x8a7020);
      for (float k = 0; k < 24; k += 3.2f) // 黄色と黒のすじ
        Poly(0x1a1408, 255, x - 12.5f + k, gy + 9.2f, x - 11 + k, gy + 9.2f, x - 12.2f + k, gy + 10.8f, x - 13.7f + k, gy + 10.8f);
      for (int s = -1; s <= 1; s += 2) // 車輪
      {
        Ball(x + s * 10, gy - 13, 1.6f, STEEL, 0.6f);
      }
      Rect(x - 9, gy - 12, 18, 2, 0x141516);
      Rect(x - 8, gy - 11.4f, 16, PX, 0x3a3a3a);
      bool weakB = B.hp[0] * 10 < B.maxhp[0] * 3;
      Disc(x, gy - 4, 4.4f, 0x2a2008);
      Ep.eye(x, gy - 4, 3, t, B.hit[0] > 0, weakB);
      if (weakB)
        Ep.sparks(x, gy - 2, t, 4);
    }

    static void drawStamper(Boss B, int t)
    {
      float cx = B.x, cy = B.y, elev = B.elev;
      for (int k = 0; k < 4; k++)
      {
        float hx, hy, kx, ky, fx, fy;
        stampLeg(B, k, t, out hx, out hy, out kx, out ky, out fx, out fy);
        if (B.hp[k + 1] <= 0) // 壊れた脚の付け根
        {
          Rod(hx, hy, hx + (fx - hx) * 0.3f, hy + 3, 2.2f, new uint[] { 0x0a0806, 0x1a1410, 0x2a2420, 0x3a3430 }, 0.2f);
          Ep.sparks(hx, hy, t + k, 2);
          continue;
        }
        bool fl = B.hit[k + 1] > 0;
        Ell(fx + 2, fy + 3, 5, 3.4f, 0, 77);
        // 太い腿 (油圧の筒) と、銀色の脚
        Rod(hx, hy, kx, ky, 2.6f, Flash(PRESS, fl), 0.4f);
        float ux = kx - hx, uy = ky - hy, l = Math.Max(0.1f, hypotf(ux, uy));
        Rod(hx + ux * 0.2f - uy / l * 1.2f, hy + uy * 0.2f + ux / l * 1.2f, hx + ux * 0.85f - uy / l * 1.2f, hy + uy * 0.85f + ux / l * 1.2f, 0.6f, STEEL, 0.8f);
        Rod(kx, ky, fx, fy, 1.6f, Flash(STEEL, fl), 0.6f);
        Ball(kx, ky, 3.2f, STEEL, 0.6f);
        Disc(kx, ky, 1.2f, 0x2a2c2e);
        Plate(fx - 4.2f, fy - 2.2f, 8.4f, 4.4f, DSTEEL, true, 0.55f, 0.35f);
        for (int s = 0; s < 3; s++)
          Rect(fx - 3 + s * 2.6f, fy + 1.2f, 1.2f, 0.9f, 0x0a0b0c);
      }
      block(cx - 34, cy - 18, 68, 36, 8, 0x5b5650, 0x3e3a36);
      // 下の型 (核の台)
      Rect(cx - 26, cy - 14, 52, 26, 0x2a2622);
      for (int k = 0; k < 6; k++)
        Rect(cx - 24 + k * 9, cy - 13, PX * 2, 24, 0x1e1a16);
      Ep.nerve(t, 0, cx - 30, cy - 20, cx - 10, cy, cx + 10, cy, cx + 30, cy - 20);
      Disc(cx, cy + 2, 7.6f, 0x1a0606);
      Ep.eye(cx, cy + 2, 5, t, B.hit[0] > 0, B.hp[0] * 3 <= B.maxhp[0]);
      foreach (int dx in new[] { -36, 28 })
        block(cx + dx, cy - 22, 8, 8, 54, 0x7a6a5c, 0x56463a);
      // ヘッド (上がると影が離れる)
      float headY = cy - 20 - elev;
      Rect(cx - 30 + elev * 0.3f, cy - 14 + elev * 0.3f, 60, 28, 0, 77);
      block(cx - 32, headY, 64, 28, 14, 0x7a6656, 0x55402f);
      for (int k = 1; k < 5; k++)
      {
        Rect(cx - 32 + k * 13, headY - 12, 2, 24, 0x5f4d40);
        Rect(cx - 32 + k * 13, headY - 12, PX, 24, 0x9a8474);
      }
      for (int k = 0; k < 8; k++) // ヘッドのボルト
      {
        float bx = cx - 29 + k * 8.2f;
        Disc(bx, headY - 12.6f, 0.6f, 0x2e241c);
        Q(bx - 0.3f, headY - 12.9f, 0xc8b4a0);
      }
      for (int i = 0; i < 6; i++) // 油のしずく
        Rect(cx - 30 + hs(i) * 60, headY + 14, 0.7f, 3 + hs(i + 2) * 6, 0x6b3a1e);
      Ep.eye(cx - 18, headY - 2, 2, t, false, false);
      Ep.eye(cx + 18, headY - 2, 2, t, false, false);
      foreach (int dx in new[] { -36, 28 })
        block(cx + dx, cy + 14, 8, 8, 54, 0x7a6a5c, 0x56463a);
      Ep.nerve(t, 3, cx - 32, cy - 34, cx - 32, cy + 10);
      Ep.nerve(t, 9, cx + 32, cy - 34, cx + 32, cy + 10);
    }

    static void drawHearth(Boss B, int t, float tx, float ty, float px, float py)
    {
      float cx = B.x, cy = B.y;
      Disc(cx + 5, cy + 7, 51, 0, 100);
      float[] bx = new float[8], by = new float[8], aim = new float[8];
      for (int k = 0; k < 8; k++)
      {
        hearthTur(B, k, tx, ty, out bx[k], out by[k], out aim[k]);
        float a = k / 8.0f * PIf * 2 + 0.2f;
        Rod(cx + cosf(a) * 42, cy + sinf(a) * 42, bx[k], by[k], 2.2f, BRICK, 0.3f);
      }
      for (int k = 0; k < 8; k++)
      {
        if (B.hp[k + 1] <= 0)
        {
          Disc(bx[k], by[k], 4.4f, 0x1a1210);
          Disc(bx[k], by[k], 2.4f, 0x0a0606);
          Ep.sparks(bx[k], by[k], t + k, 1);
          continue;
        }
        bool fl = B.hit[k + 1] > 0;
        // 羽口 (炉に風を送る口。いまは砲台)
        Rod(bx[k], by[k], bx[k] + cosf(aim[k]) * 8, by[k] + sinf(aim[k]) * 8, 1.6f, DSTEEL, 0.5f);
        Disc(bx[k] + cosf(aim[k]) * 8.4f, by[k] + sinf(aim[k]) * 8.4f, 0.9f, 0xff7a2a);
        Ball(bx[k], by[k], 5.3f, Flash(RUSTR, fl), 0.5f);
        Ring(bx[k], by[k], 3.6f, PX, 0x2e170d);
        Disc(bx[k], by[k], 1.4f, 0x1a0606);
        Disc(bx[k] - 0.3f, by[k] - 0.3f, 0.8f, Ep.RED);
      }
      // 炉の外壁 (れんがの輪)
      Ball(cx, cy, 46.5f, BRICK, 0.15f, true, 0.25f);
      for (int r = 0; r < 3; r++) // れんがの目地
      {
        float rr = 38 + r * 3;
        Ring(cx, cy, rr, PX, 0x2a1810);
        int n = 28 + r * 2;
        for (int k = 0; k < n; k++)
        {
          float a = (k + (r % 2) * 0.5f) / n * PIf * 2;
          Line1(cx + cosf(a) * rr, cy + sinf(a) * rr, cx + cosf(a) * (rr + 3), cy + sinf(a) * (rr + 3), 0x2a1810);
        }
      }
      Ring(cx, cy, 46, 1.0f, 0x8a4f28);
      Ring(cx, cy, 43, 0.8f, 0x3a2216);
      Disc(cx, cy, 37, 0x2a1c16);
      Ring(cx, cy, 36, 1.0f, 0x1a100c);
      // とけた鉄 (脈打つ)
      float pul = sinf(t * 0.09f);
      Disc(cx, cy, 32.5f, 0x5a1204);
      Disc(cx, cy, 31, 0x7a1d05);
      for (int k = 0; k < 360; k += 3) // 鉄の面のゆらぎ (橙の模様)
      {
        float a = k * PIf / 180, rr = 20 + 10 * hs(k * 0.37f);
        float v = sinf(a * 5 + t * 0.07f) * 0.5f + 0.5f;
        if (v > 0.55f)
          Disc(cx + cosf(a) * rr, cy + sinf(a) * rr, 1.2f + v * 1.2f, 0xc2410c);
      }
      Disc(cx, cy, 26, 0xc2410c, 200);
      Disc(cx, cy, 21, pul > 0 ? 0xf26b1du : 0xe85d12u);
      Disc(cx - 2, cy - 2, 15, 0xff8a2a, 120);
      for (int k = 0; k < 7; k++) // 浮かぶ泡
      {
        float a = t * 0.03f + k * 0.9f, rr = 22 + 4 * sinf(t * 0.05f + k);
        float bxx = cx + cosf(a) * rr, byy = cy + sinf(a) * rr;
        Disc(bxx, byy, 2.2f, 0xffb23e);
        Disc(bxx - 0.6f, byy - 0.6f, 1.0f, 0xfff0c0);
      }
      for (int k = 0; k < 8; k++)
        if (B.hp[k + 1] > 0)
          Ep.nerve(t, k * 3, cx, cy, bx[k], by[k]);
      // 真ん中の目
      Disc(cx, cy, 15.5f, 0x1f0806);
      Ring(cx, cy, 15, 0.9f, Ep.RED2);
      Ring(cx, cy, 13, 0.8f, Ep.RED3);
      for (int k = 0; k < 12; k++) // 目のまわりの金具
      {
        float a = k * PIf / 6;
        Disc(cx + cosf(a) * 14, cy + sinf(a) * 14, 0.6f, 0x5a1c14);
      }
      bool fl0 = B.hit[0] > 0;
      Disc(px, py, 9.5f, Ep.RED, 30);
      Disc(px, py, 7.5f, fl0 ? 0xffffffu : Ep.RED2);
      Ball(px, py, 5.6f, fl0 ? Flash(DARKR, true) : new uint[] { 0x5a0c08, 0x8a160e, Ep.RED2, Ep.RED, 0xff7a6a, 0xffd0c8 }, 0.8f, false, 0.4f);
      Disc(px, py, 2.2f, 0x200404);
      Disc(px - 3.2f, py - 3.2f, 0.9f, Ep.REDW);
      if ((B.t % 140) < 6) // まばたき (上下からまぶたが閉じる)
      {
        float q = (B.t % 140) / 6.0f, hh = 15.5f * sinf(q * PIf);
        Rect(cx - 15.5f, cy - 15.5f, 31, hh, 0x3d2a22);
        Rect(cx - 15.5f, cy + 15.5f - hh, 31, hh, 0x3d2a22);
        Rect(cx - 15.5f, cy - 15.5f + hh - 0.6f, 31, 0.6f, 0x8a5a3a);
      }
      if (B.hp[0] * 3 <= B.maxhp[0])
        Ep.sparks(cx, cy, t, 6);
    }

    // air: ガントリーは頭上なので手前に、ほかは床なので戦車より下に描く
    void bossDraw(bool air)
    {
      Boss B = boss;
      if (B.k < 0)
        return;
      if (!air && B.ringOn)
      {
        float q = B.ringR / 85;
        Ring(B.ringX, B.ringY, B.ringR, 1.2f, 0xe8fffb, (int)(200 * (1 - q)));
        Ring(B.ringX, B.ringY, B.ringR - 3, 1.0f, 0xe8fffb, (int)(110 * (1 - q)));
        for (int k = 0; k < 24; k++) // 舞い上がる土ぼこり
        {
          float a = k * PIf / 12 + hs(k) * 0.2f;
          Disc(B.ringX + cosf(a) * B.ringR, B.ringY + sinf(a) * B.ringR * 0.9f, 1.4f, 0xb0a690, (int)(90 * (1 - q)));
        }
      }
      if ((B.k == 0) != air)
        return;
      if (B.dieT > 60 && (t & 2) != 0) // 消える前に点滅
        return;
      if (B.k == 0)
        drawGantry(B, t);
      else if (B.k == 1)
        drawStamper(B, t);
      else
        drawHearth(B, t, tx, ty, bossEyeX(), bossEyeY());
      if (B.stun > 0 && (B.stun > 30 || (t & 4) != 0))
      {
        if (B.k == 0)
          stunNoise(B.x, B.y, 30, 20, t);
        else
          stunNoise(B.x, B.y, 44, 36, t);
      }
    }
  }
}
