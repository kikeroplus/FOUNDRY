// FOUNDRY: 捨てられた工場を、六本脚の戦車が上へ登っていく縦シューティング。ESP32 版 (SkyLarkESP32 の game2.cpp) を移したもの。
// 敵は、工業機械に乗り移った AI。味方の弾は青、敵の目と弾は赤。
// 遊びの流れ: デモ (タイトル) → 遊ぶ → 残機がなくなったらコンティニューの秒読み → ゲームオーバー → 名前 → ランキング → デモ
// 世界の座標・当たり判定・速さ・出る表は ESP32 版と同じ。描くのは 3 倍の細かさ (Gfx.cs)
using System;
using System.Collections.Generic;

namespace Foundry
{
  public class Input
  {
    public bool Left, Right, Up, Down, Fire, Slow, Bomb;
    public float AX, AY; // スティック (-1〜1)
    public bool FirePressed, StartPressed, PausePressed, QuitPressed, BackPressed, BombPressed;
    public bool UpPressed, DownPressed, LeftPressed, RightPressed;
    public char Typed; // ネームエントリーでキーボードから打った文字 ('\r' で決める、'\b' で戻る)
  }

  public class Rank
  {
    public int Score;
    public string Name;
    public Rank(int s, string n)
    {
      Score = s;
      Name = n;
    }
  }

  public partial class Game : D
  {
    public const int FPS = 30;

    public Rank[] Table = { new Rank(50000, "IRN"), new Rank(40000, "LEG"), new Rank(30000, "RST"), new Rank(20000, "FOU"), new Rank(10000, "NDR") };
    public string LastName = "AAA";
    public Action TableChanged; // ランキングが変わったら呼ぶ (保存する)
    public Sound Snd;
    public bool autoPilot;      // 確かめる用: 遊んでいるあいだも自動で歩いて撃ち、やられない

    public class Shot
    {
      public float x, y, dx, dy;
      public bool dead;
      public int k;    // 敵の弾: 0 赤いひし形 / 1 溶接の火花 (ジグザグ) / 2 橙の大きな弾 (ボス) / 3 蒸気 (近くだけ)
      public int life; // 0 でなければ、この数のフレームで消える
      public Shot(float x, float y, float dx, float dy, int k = 0, int life = 0)
      {
        this.x = x;
        this.y = y;
        this.dx = dx;
        this.dy = dy;
        this.k = k;
        this.life = life;
      }
    }
    public class Fx
    {
      public int k; // 0 爆発 / 2 戦車の大きな爆発 / 3 弾をはじいた火花 / 4 核が乗り移った輪 / 5 小さい爆発 / 6 爆弾の爆風
      public float x, y, s;
      public int t, life;
      public bool ground;
      public Fx(int k, float x, float y, float s, int t, int life, bool ground)
      {
        this.k = k;
        this.x = x;
        this.y = y;
        this.s = s;
        this.t = t;
        this.life = life;
        this.ground = ground;
      }
    }
    class Foot
    {
      public float x, y, sx, sy, p; // p: 足を上げている途中 (0〜1)。-1 は床に着いている
    }
    class Item
    {
      public float x, y;
      public int t;
    }

    public enum Mode
    {
      Attract, // デモ (自動で歩いて撃つ。タイトルとランキングを交互に出す)
      Play,
      Continue, // 残機がなくなった。数えているあいだに撃つボタンで続ける
      GameOver,
      NameEntry,
      Ranking, // 遊び終わったあとのランキング
    }

    static readonly float SPEED = 0.55f; // 背景が流れる速さ (点/フレーム)
    static readonly float[,] HIPS = { { -8, -8 }, { 8, -8 }, { -10, 0 }, { 10, 0 }, { -8, 8 }, { 8, 8 } };
    static readonly float[,] REST = { { -19, -18 }, { 19, -18 }, { -23, 1 }, { 23, 1 }, { -19, 18 }, { 19, 18 } };
    static readonly int[] GRP = { 0, 1, 1, 0, 0, 1 }; // 0: 左前・右中・左後 / 1: 右前・左中・右後 (3 本ずつ交互に上げる)
    const string LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-! ";
    const int BOMB_FLIGHT = 18, BOMB_COOL = 40; // 爆弾が飛んでいる時間、次に投げられるまで
    const float BOMB_RANGE = 80;               // 戦車からどれだけ先に落ちるか
    const int ENTER_FRAMES = 40, INV_FRAMES = 100, DEAD_FRAMES = 50;

    public Mode mode = Mode.Attract;
    int modeT;
    public bool Paused;
    float scroll, tx = 120, ty = 250, ang = -1.5708f, rec;
    int t, fireCool;
    public int Score;
    int lives = 2, nextExtend = 40000, continues, extendT;
    bool tankOn = true; // 戦車が画面にいる (やられているあいだと遊び終わったあとは false)
    int deadT, invT, enterT;
    int bannerT, bannerZone = -1, lastZone = -1;
    char[] entry = { 'A', 'A', 'A' };
    int entryPos, entryRepeat, newRank = -1;
    readonly Foot[] feet = new Foot[6];
    List<Shot> shots = new List<Shot>(), eb = new List<Shot>();
    List<Fx> fx = new List<Fx>();
    // 弾を強くする部品 (青く光るエネルギーの電池)。拾うと shotLv が上がる (0〜3)。やられると 1 段下がる
    List<Item> items = new List<Item>();
    int shotLv, killCount, powT;
    // 爆弾: 砲塔の向きの少し先へ放り投げ、落ちたところで爆発して、まわりの敵に少し傷を負わせる
    bool bombOn;
    float bombX0, bombG0, bombX1, bombG1; // 投げた所と落ちる所 (y は床の位置)
    int bombT, bombCool;
    float spd = 0.55f; // 今の流れる速さ
    int pileT = 40, clankT = 90; // 背景のくい打ち・大きな機械が次に鳴るまで
    int bossBannerT; // 「WARNING」や「倒した」を出す残り
    int bossResult;  // 0 なし / 1 倒した / 2 逃げられた

    // 床の物の位置に足す流れ (細かい点の単位でそろえる。背景と同じだけずらす)
    float SC { get { return (float)Math.Floor(scroll * S) / S; } }

    public Game()
    {
      for (int i = 0; i < 6; i++)
        feet[i] = new Foot();
    }

    void sfx(Sfx s, float vol = 1)
    {
      if (Snd != null)
        Snd.Play(s, mode == Mode.Attract ? vol * 0.5f : vol); // デモは小さめ
    }

    // 鳴り続ける音: 区画で変える。溶鉱炉はごうごう、ボスのあいだは低いうなり
    void updateAmbience()
    {
      if (Snd == null)
        return;
      bool quiet = Paused || mode == Mode.Continue || mode == Mode.NameEntry;
      int z = lastZone < 0 ? 0 : lastZone;
      float k = quiet ? 0 : mode == Mode.Attract ? 0.5f : 1;
      Snd.Hum = (z == 0 ? 0.5f : z == 7 ? 0.6f : 1.0f) * k;
      if (k > 0) // 遠くのくい打ち (ガーン、ときどきガーン、ガーン) と、大きな機械 (ガシャン・シュー)
      {
        if (--pileT <= 0)
        {
          Snd.Play(Sfx.PileDrive, (0.5f + hs(t) * 0.5f) * k);
          pileT = hs(t * 1.7f) < 0.3f ? 14 : 36 + (int)(hs(t * 2.3f) * 20);
        }
        if (--clankT <= 0)
        {
          Snd.Play(Sfx.MachClank, (0.5f + hs(t + 9) * 0.5f) * k);
          clankT = 80 + (int)(hs(t * 3.1f) * 90);
        }
      }
      Snd.Roar = z == 7 ? 1.0f * k : 0;
      Snd.MomHum = boss.k >= 0 && boss.dieT == 0 ? 0.55f * k : 0;
    }

    public void Begin()
    {
      Bg.Build();
      startDemo();
    }

    void initFeet()
    {
      float sc = SC;
      for (int i = 0; i < 6; i++)
      {
        feet[i].x = tx + REST[i, 0];
        feet[i].y = ty - sc + REST[i, 1];
        feet[i].sx = feet[i].sy = 0;
        feet[i].p = -1;
      }
    }

    // ---- 流れ ----
    void resetWorld()
    {
      scroll = 0;
      t = 0;
      shots.Clear();
      eb.Clear();
      en.Clear();
      fx.Clear();
      lastZone = -1;
      bannerT = 0;
      extendT = 0;
      deadT = invT = enterT = 0;
      fireCool = 0;
      ang = -PIf / 2;
      pendingLvl = 0;
      resetSpawns(true);
      boss = new Boss();
      spd = SPEED;
      bossBannerT = 0;
      items.Clear();
      bombOn = false;
      bombCool = 0;
      shotLv = 0;
      killCount = 0;
      powT = 0;
    }

    void startDemo()
    {
      mode = Mode.Attract;
      modeT = 0;
      Paused = false;
      resetWorld();
      Score = 0;
      tankOn = true;
      tx = W / 2;
      ty = 240;
      initFeet();
    }

    void newGame()
    {
      mode = Mode.Play;
      Paused = false;
      resetWorld();
      Score = 0;
      lives = 2;
      nextExtend = 40000;
      continues = 0;
      sfx(Sfx.G2Start);
      enterTank();
    }

    void enterTank()
    {
      tankOn = true;
      tx = W / 2;
      ty = H + 30;
      enterT = ENTER_FRAMES;
      invT = ENTER_FRAMES + INV_FRAMES;
      ang = -PIf / 2;
      eb.Clear();
      initFeet();
    }

    void killTank()
    {
      sfx(Sfx.Death);
      tankOn = false;
      deadT = DEAD_FRAMES;
      shotLv = max(0, shotLv - 1); // 弾の強さは 1 段下がる
      fx.Add(new Fx(2, tx, ty, hs(t) * 6, 0, 40, true));
      for (int i = 0; i < 4; i++)
        fx.Add(new Fx(0, tx + (hs(t + i) - 0.5f) * 30, ty + (hs(t * 2 + i) - 0.5f) * 26, hs(t + i * 3) * 6, -i * 5, 26, true));
    }

    void endGame()
    {
      mode = Mode.GameOver;
      modeT = 4 * FPS;
      tankOn = false;
      sfx(Sfx.GameOver);
    }

    int rankOf(int s)
    {
      for (int i = 0; i < 5; i++)
        if (s > Table[i].Score)
          return i;
      return -1;
    }

    // 名前の 3 文字: 上下で文字を選び、撃つボタンで決めて次の文字へ (左か戻るボタンで戻る)。キーボードで直接打ってもよい
    void stepEntry(Input inp)
    {
      if (--modeT <= 0)
      {
        finishEntry();
        return;
      }
      if (inp.Typed == '\r')
      {
        finishEntry();
        return;
      }
      if (inp.Typed == '\b')
      {
        entryPos = max(0, entryPos - 1);
        return;
      }
      if (inp.Typed != '\0' && LETTERS.IndexOf(inp.Typed) >= 0)
      {
        entry[entryPos] = inp.Typed;
        sfx(Sfx.Select);
        if (entryPos < 2)
          entryPos++;
        return;
      }
      int dir = 0;
      if (inp.UpPressed)
        dir = 1;
      else if (inp.DownPressed)
        dir = -1;
      else if (inp.Up || inp.Down)
      {
        if (++entryRepeat > 8 && entryRepeat % 3 == 0)
          dir = inp.Up ? 1 : -1;
      }
      if (!inp.Up && !inp.Down)
        entryRepeat = 0;
      if (dir != 0)
      {
        sfx(Sfx.Select);
        int n = LETTERS.Length, k = LETTERS.IndexOf(entry[entryPos]);
        entry[entryPos] = LETTERS[(k + dir + n) % n];
      }
      if (inp.LeftPressed || inp.BackPressed)
        entryPos = max(0, entryPos - 1);
      if (inp.RightPressed && entryPos < 2)
        entryPos++;
      if (inp.FirePressed)
      {
        sfx(Sfx.Select);
        if (entryPos < 2)
          entryPos++;
        else
          finishEntry();
      }
    }

    void finishEntry()
    {
      string name = new string(entry).TrimEnd(' ');
      if (name.Length == 0)
        name = "---";
      LastName = new string(entry);
      int i = rankOf(Score);
      if (i >= 0)
      {
        for (int j = 4; j > i; j--)
          Table[j] = Table[j - 1];
        Table[i] = new Rank(Score, name);
        newRank = i;
        if (TableChanged != null)
          TableChanged();
      }
      mode = Mode.Ranking;
      modeT = 8 * FPS;
    }

    public void Step(Input inp)
    {
      SavePrev();
      updateAmbience();
      switch (mode)
      {
        case Mode.Attract:
          if (inp.FirePressed || inp.StartPressed)
          {
            newGame();
            return;
          }
          modeT++;
          stepWorld(null);
          break;
        case Mode.Play:
          if (inp.PausePressed)
          {
            Paused = !Paused;
            sfx(Sfx.Pause);
          }
          if (Paused)
          {
            if (inp.QuitPressed)
              startDemo();
            return;
          }
          stepWorld(inp);
          break;
        case Mode.Continue:
          if (inp.FirePressed || inp.StartPressed)
          {
            mode = Mode.Play;
            lives = 2;
            continues++;
            sfx(Sfx.G2Start);
            if (Score % 10 < 9) // 点のいちばん下の桁は、コンティニューした回数
              Score++;
            enterTank();
            return;
          }
          if (inp.PausePressed || inp.QuitPressed) // やめる
            modeT = 1;
          if (modeT % FPS == 0 && modeT > 0)
            sfx(Sfx.Tick);
          if (--modeT <= 0)
            endGame();
          break;
        case Mode.GameOver:
          stepWorld(null);
          if (--modeT <= 0 || (modeT < 3 * FPS && (inp.FirePressed || inp.StartPressed)))
          {
            if (rankOf(Score) >= 0)
            {
              mode = Mode.NameEntry;
              modeT = 40 * FPS;
              for (int i = 0; i < 3; i++)
                entry[i] = i < LastName.Length && LETTERS.IndexOf(LastName[i]) >= 0 ? LastName[i] : 'A';
              entryPos = 0;
            }
            else
            {
              mode = Mode.Ranking;
              newRank = -1;
              modeT = 6 * FPS;
            }
          }
          break;
        case Mode.NameEntry:
          stepWorld(null);
          stepEntry(inp);
          break;
        case Mode.Ranking:
          stepWorld(null);
          if (--modeT <= 0 || (modeT < 7 * FPS && (inp.FirePressed || inp.StartPressed)))
            startDemo();
          break;
      }
    }

    // 世界を 1 フレーム進める。戦車は、遊んでいるときは inp で、デモでは自動で動かす
    void stepWorld(Input inp)
    {
      t++;
      spd += ((boss.k >= 0 && boss.dieT == 0 ? 0.0f : SPEED) - spd) * 0.04f; // ボスのあいだは流れが止まる
      scroll += spd;
      bossStep();
      if (tankOn)
      {
        if (mode == Mode.Attract || (autoPilot && mode == Mode.Play && enterT == 0))
        {
          float gx = 120 + 48 * sinf(t * 0.011f) + 14 * sinf(t * 0.029f), gy = 236 + 12 * sinf(t * 0.017f);
          stepTank(clampf((gx - tx) / 2, -1, 1), clampf((gy - ty) / 2, -1, 1), true, false);
        }
        else if (enterT > 0)
        {
          enterT--;
          stepTank(0, -1, false, false);
        }
        else if (inp != null)
          stepTank(inp.AX, inp.AY, inp.Fire, inp.Slow);
        else
          stepTank(0, 0, false, false);
        bool wantBomb = (inp != null && inp.BombPressed) || (autoPilot && t % 50 == 0);
        if (wantBomb && mode == Mode.Play && enterT == 0 && !bombOn && bombCool == 0) // 爆弾を投げる
        {
          float sc = SC;
          bombOn = true;
          sfx(Sfx.Bomb, 0.6f);
          bombT = 0;
          bombCool = BOMB_COOL;
          bombX0 = tx;
          bombG0 = ty - 6 - sc;
          bombX1 = clampf(tx + cosf(ang) * BOMB_RANGE, 12, W - 12);
          bombG1 = ty + sinf(ang) * BOMB_RANGE - sc;
        }
      }
      if (invT > 0)
        invT--;
      if (deadT > 0 && --deadT == 0 && mode == Mode.Play)
      {
        if (lives > 0)
        {
          lives--;
          enterTank();
        }
        else
        {
          mode = Mode.Continue;
          modeT = 10 * FPS - 1;
        }
      }

      enemyStep();

      if (mode == Mode.Play && tankOn && invT == 0 && !autoPilot)
      {
        bool hit = false;
        foreach (Shot b in eb)
          if (!b.dead && b.k != 3 && (b.k == 2 ? hypotf(b.x - tx, b.y - ty) < 7 : fabsf(b.x - tx) < 5 && fabsf(b.y - ty) < 7))
          {
            b.dead = true;
            hit = true;
          }
        foreach (Shot b in eb)
          if (!b.dead && b.k == 3 && hypotf(b.x - tx, b.y - ty) < 9)
            hit = true;
        foreach (Enemy e in en)
          if (touchesTank(e))
            hit = true;
        if (hit)
          killTank();
      }
      if (mode == Mode.Play && Score >= nextExtend)
      {
        lives++;
        sfx(Sfx.Extend);
        nextExtend += 60000;
        extendT = 2 * FPS;
      }
      if (extendT > 0)
        extendT--;
      if (powT > 0)
        powT--;
      if (bombCool > 0)
        bombCool--;
      if (bombOn && ++bombT >= BOMB_FLIGHT)
      {
        bombOn = false;
        bombBlast(bombX1, bombG1 + SC);
      }
      foreach (Item it in items) // 部品は少しゆれながら流れてくる。戦車が触れたら拾う
      {
        it.t++;
        it.y += 0.7f;
        it.x += sinf(it.t * 0.06f) * 0.4f;
        if (tankOn && enterT == 0 && hypotf(it.x - tx, it.y - ty) < 16)
        {
          it.y = H + 100;
          if (shotLv < 3)
          {
            shotLv++;
            sfx(Sfx.Power);
            powT = 2 * FPS;
          }
          else
          {
            if (mode == Mode.Play)
              Score += 1000;
            sfx(Sfx.Power, 0.6f);
          }
        }
      }
      items.RemoveAll(i => i.y > H + 12);
      int z = zoneAt(H / 2);
      if (z != lastZone)
      {
        lastZone = z;
        if (mode == Mode.Play)
        {
          bannerZone = z;
          bannerT = 3 * FPS;
        }
      }
      if (bannerT > 0)
        bannerT--;
      eb.RemoveAll(s => s.dead);
      shots.RemoveAll(s => s.dead);
    }

    void stepTank(float ax, float ay, bool fire, bool slow)
    {
      float sc = SC;
      float sp = slow ? 1.0f : 2.0f;
      float ntx = clampf(tx + ax * sp, 26, W - 26), nty = ty + ay * sp;
      if (enterT == 0)
        nty = clampf(nty, 110, H - 24);
      float vx = ntx - tx, vy = nty - ty - spd;
      tx = ntx;
      ty = nty;

      // 脚: 床に着いた足は床の位置 (y は流れる前の座標) のまま。離れすぎた組から 3 本ずつ踏み出す
      float bx = tx, by = ty - sc;
      bool stepping = false;
      foreach (Foot f in feet)
        if (f.p >= 0)
          stepping = true;
      if (!stepping)
      {
        float[] err = { 0, 0 };
        for (int i = 0; i < 6; i++)
          err[GRP[i]] = max(err[GRP[i]], hypotf(bx + REST[i, 0] - feet[i].x, by + REST[i, 1] - feet[i].y));
        int g = err[0] >= err[1] ? 0 : 1;
        if (err[g] > 6)
          for (int i = 0; i < 6; i++)
            if (GRP[i] == g)
            {
              feet[i].p = 0;
              feet[i].sx = feet[i].x;
              feet[i].sy = feet[i].y;
            }
      }
      bool landed = false;
      for (int i = 0; i < 6; i++)
      {
        Foot f = feet[i];
        if (f.p < 0)
          continue;
        f.p = min(1.0f, f.p + 1 / 7.0f);
        float gx = bx + REST[i, 0] + vx * 5, gy = by + REST[i, 1] + vy * 5;
        float e = f.p * f.p * (3 - 2 * f.p);
        f.x = f.sx + (gx - f.sx) * e;
        f.y = f.sy + (gy - f.sy) * e;
        if (f.p >= 1)
        {
          f.p = -1;
          landed = true;
        }
      }

      if (landed && enterT == 0) // 3 本の足が着いた: カシャ
        sfx(Sfx.Step, mode == Mode.Attract ? 0.5f : 1);

      // 砲塔: いちばん近い前の敵へ向く (上から左右 0.6 まで)
      Enemy best = null;
      float bd = 1e9f;
      foreach (Enemy e in en)
      {
        if (e.hidden || e.y > ty - 10 || e.y < 0)
          continue;
        float d = hypotf(e.x - tx, e.y - ty);
        if (d < bd)
        {
          bd = d;
          best = e;
        }
      }
      float ta = -PIf / 2;
      if (best != null)
        ta = clampf(atan2f(best.y - ty, best.x - tx), -PIf / 2 - 0.6f, -PIf / 2 + 0.6f);
      ang += (ta - ang) * 0.15f;

      if (fireCool > 0)
        fireCool--;
      if (fire && fireCool == 0 && ty < H - 10)
      {
        // 弾の強さ: 0 平行に 2 発 / 1 斜めに 2 発足す / 2 連射が速くなる / 3 さらに外側へ 2 発
        float dx = cosf(ang), dy = sinf(ang);
        foreach (float s in new[] { -2.5f, 2.5f })
          shots.Add(new Shot(tx - dy * s + dx * 16, ty - 1 + dx * s + dy * 16, dx * 7, dy * 7));
        if (shotLv >= 1)
          foreach (float d in new[] { -0.2f, 0.2f })
            shots.Add(new Shot(tx + dx * 12, ty - 1 + dy * 12, cosf(ang + d) * 7, sinf(ang + d) * 7));
        if (shotLv >= 3)
          foreach (float d in new[] { -0.42f, 0.42f })
            shots.Add(new Shot(tx + dx * 10, ty - 1 + dy * 10, cosf(ang + d) * 7, sinf(ang + d) * 7));
        rec = 3;
        sfx(Sfx.G2Shot, mode == Mode.Attract ? 0.4f : 0.8f);
        fireCool = shotLv >= 2 ? 4 : 6;
      }
      rec = max(0.0f, rec - 0.6f);
    }

    int zoneAt(int sy)
    {
      int sc = (int)scroll;
      int wu = ((sc + H - sy) % Bg.TOT + Bg.TOT) % Bg.TOT; // 下から数えた位置
      int z = 0;
      for (int i = 0; i < Bg.NZ; i++)
        if (wu >= Bg.STARTS[i])
          z = i;
      return z;
    }

    // 確かめる用: 次の区画の始まりへ進める
    public void SkipZone()
    {
      int sc = (int)scroll, TOT = Bg.TOT;
      int wu = sc + H + LEAD + 2, bs = wu / TOT * TOT; // 画面の上端 (出る線) より先の、次の区画の始まり
      int next = bs + TOT;
      for (int i = 0; i < Bg.NZ; i++)
        if (bs + Bg.STARTS[i] > wu)
        {
          next = bs + Bg.STARTS[i];
          break;
        }
      scroll = next - H - LEAD - 1; // 次の区画の始まりが、ちょうど出る線の手前に来る
      en.Clear();
      born.Clear();
      eb.Clear();
      fx.Clear();
      boss = new Boss();
      spd = 0.55f;
      for (int g = 0; g < 400; g++)
      {
        if (evIdx >= NEV)
        {
          evIdx = 0;
          lap++;
        }
        if (evPos(evIdx, lap) > scroll + H + LEAD)
          break;
        evIdx++;
      }
      initFeet();
      SavePrev();
    }
    public void StartPlay()
    {
      newGame();
    }
    public int Zone { get { return lastZone < 0 ? 0 : lastZone; } }

    // ================= 描く =================
    // 1 秒 60 回描くときは、前の 1 歩と今の 1 歩のあいだ (a) の位置で描く。描いたあとは元に戻す
    float pScroll, pTx, pTy, pAng, pBossX, pBossY;
    readonly float[] pFx = new float[6], pFy = new float[6];
    void SavePrev()
    {
      pScroll = scroll;
      pTx = tx;
      pTy = ty;
      pAng = ang;
      pBossX = boss.x;
      pBossY = boss.y;
      for (int i = 0; i < 6; i++)
      {
        pFx[i] = feet[i].x;
        pFy[i] = feet[i].y;
      }
      foreach (Enemy e in en)
      {
        e.px = e.x;
        e.py = e.y;
      }
    }

    static float Lerp(float a, float b, float k) { return a + (b - a) * k; }

    public void Draw(int[] screen, float a = 1)
    {
      // あいだの位置に置きかえる (大きく跳んだものはそのまま)
      float cScroll = scroll, cTx = tx, cTy = ty, cAng = ang, cBx = boss.x, cBy = boss.y;
      float[] cFx = new float[6], cFy = new float[6];
      List<float> keep = new List<float>();
      bool lerp = a < 0.999f && !Paused && mode != Mode.Continue;
      if (lerp)
      {
        float b = 1 - a;
        scroll = Lerp(pScroll, scroll, a);
        if (Math.Abs(tx - pTx) < 8 && Math.Abs(ty - pTy) < 8)
        {
          tx = Lerp(pTx, tx, a);
          ty = Lerp(pTy, ty, a);
        }
        ang = Lerp(pAng, ang, a);
        if (Math.Abs(boss.x - pBossX) < 8 && Math.Abs(boss.y - pBossY) < 8)
        {
          boss.x = Lerp(pBossX, boss.x, a);
          boss.y = Lerp(pBossY, boss.y, a);
        }
        for (int i = 0; i < 6; i++)
        {
          cFx[i] = feet[i].x;
          cFy[i] = feet[i].y;
          if (Math.Abs(feet[i].x - pFx[i]) < 12 && Math.Abs(feet[i].y - pFy[i]) < 12)
          {
            feet[i].x = Lerp(pFx[i], feet[i].x, a);
            feet[i].y = Lerp(pFy[i], feet[i].y, a);
          }
        }
        foreach (Enemy e in en)
        {
          keep.Add(e.x);
          keep.Add(e.y);
          if (Math.Abs(e.x - e.px) < 12 && Math.Abs(e.y - e.py) < 12)
          {
            e.x = Lerp(e.px, e.x, a);
            e.y = Lerp(e.py, e.y, a);
          }
        }
        foreach (Shot s in shots)
        {
          s.x -= s.dx * b;
          s.y -= s.dy * b;
        }
        foreach (Shot s in eb)
        {
          s.x -= s.dx * b;
          s.y -= s.dy * b;
        }
        float dsc = cScroll - scroll; // 床に乗った爆発は流れと一緒に戻す
        foreach (Fx f in fx)
          if (f.ground)
            f.y -= dsc;
        foreach (Item it in items)
          it.y -= 0.7f * b;
      }

      DrawFrame(screen);

      if (lerp)
      {
        float b = 1 - a;
        float dsc = cScroll - scroll;
        scroll = cScroll;
        tx = cTx;
        ty = cTy;
        ang = cAng;
        boss.x = cBx;
        boss.y = cBy;
        for (int i = 0; i < 6; i++)
        {
          feet[i].x = cFx[i];
          feet[i].y = cFy[i];
        }
        int n = 0;
        foreach (Enemy e in en)
        {
          e.x = keep[n++];
          e.y = keep[n++];
        }
        foreach (Shot s in shots)
        {
          s.x += s.dx * b;
          s.y += s.dy * b;
        }
        foreach (Shot s in eb)
        {
          s.x += s.dx * b;
          s.y += s.dy * b;
        }
        foreach (Fx f in fx)
          if (f.ground)
            f.y += dsc;
        foreach (Item it in items)
          it.y += 0.7f * b;
      }
    }

    readonly Surf screenSurf = new Surf();

    void DrawFrame(int[] screen)
    {
      int TOT = Bg.TOT, TOTP = TOT * S;
      float sc = SC;
      int scPx = (int)Math.Round(sc * S);
      // 背景 (取っておいた絵を写す)
      for (int py = 0; py < PH; py++)
      {
        int r = ((TOTP - PH - scPx + py) % TOTP + TOTP) % TOTP;
        Array.Copy(Bg.bgBuf, r * PW, screen, py * PW, PW);
      }
      Bg.hideCrane = boss.k == 0;
      Surf s = screenSurf;
      s.Buf = screen;
      s.Base = 0;
      s.Y0 = 0;
      s.Hpx = PH;
      s.OX = s.OY = 0;
      s.CX0 = 0;
      s.CX1 = PW - 1;
      Sf = s;
      // 区画の動くもの
      for (int k = (int)Math.Floor(sc / TOT) - 1; k <= (int)Math.Floor((sc + H) / TOT) + 1; k++)
        for (int i = 0; i < Bg.NZ; i++)
        {
          Zone z = Bg.ZONES[i];
          float z0 = k * TOT + Bg.STARTS[i], top = H - (z0 + z.H - sc);
          if (top + z.H <= 0 || top >= H)
            continue;
          s.OY = top;
          int tp = (int)Math.Round(top * S);
          s.CY0 = Math.Max(tp, 0);
          s.CY1 = Math.Min(tp + z.H * S - 1, PH - 1);
          z.Anim(z.H, t);
        }
      s.OY = 0;
      s.CY0 = 0;
      s.CY1 = PH - 1;
      // 地上の敵、ボス、自分の弾、戦車
      drawEnemies(false);
      bossDraw(false);
      foreach (Shot b in shots)
        drawShot(b.x, b.y, b.dx / 7, b.dy / 7, t);
      bool blink = invT > 0 && enterT == 0 && (t & 2) != 0; // 入り直したあとの点滅
      if (tankOn && !blink && ty > -40 && ty < H + 40)
      {
        float[] fxs = new float[6], fys = new float[6], lift = new float[6];
        for (int i = 0; i < 6; i++)
        {
          fxs[i] = feet[i].x;
          fys[i] = feet[i].y + sc;
          lift[i] = feet[i].p >= 0 ? sinf(PIf * feet[i].p) : 0;
        }
        drawTank(tx, ty, fxs, fys, lift, ang, rec, t, invT > 0 && enterT == 0);
      }
      // 手前の絵 (頭上の配管や梁)
      for (int py = 0; py < PH; py++)
      {
        int r = ((TOTP - PH - scPx + py) % TOTP + TOTP) % TOTP;
        if (!Bg.foreRow[r])
          continue;
        int src = r * PW, dst = py * PW;
        int[] fb = Bg.foreBuf;
        for (int x = 0; x < PW; x++)
        {
          int v = fb[src + x];
          if (v != 0)
            screen[dst + x] = v;
        }
      }
      // 空の敵、爆発、敵の弾
      if (bombOn) // 飛んでいる爆弾: 弧を描いて落ちる (影は床に)
      {
        float p = (float)bombT / BOMB_FLIGHT, bx = bombX0 + (bombX1 - bombX0) * p, gy = bombG0 + (bombG1 - bombG0) * p + sc;
        float by = gy - sinf(p * PIf) * 22;
        drawBomb(bx, by, gy, t);
      }
      foreach (Item it in items)
        drawItem(it.x, it.y, it.t);
      bossDraw(true);
      drawEnemies(true);
      foreach (Fx f in fx)
        if (f.t >= 0)
        {
          if (f.k == 2)
            drawBigBoom(f.x, f.y, f.s, f.t, f.life);
          else
            drawFx(f.k, f.x, f.y, f.s, f.t, f.life);
        }
      foreach (Shot b in eb)
        drawEShot(b.k, b.x, b.y, b.dx, b.dy, b.life, t);
      drawOverlay();
    }

    // ================= 戦車 =================
    static readonly uint[] STEEL = { 0x16181a, 0x2e3236, 0x4a5056, 0x6a7178, 0x8c939a, 0xb2b8be, 0xdadee2, 0xf6f8f9 };
    static readonly uint[] DSTEEL = { 0x0e0f10, 0x1e2124, 0x33373b, 0x4a4f54, 0x656b71, 0x868c92 };
    static readonly uint[] GUN = { 0x0c0d0e, 0x1e2022, 0x34383c, 0x50565c, 0x7a8187, 0xaab0b6 };
    static readonly uint[] BLUE = { 0x0a1a3a, 0x163a78, 0x2a64c8, 0x3a7bff, 0x6ab8ff, 0xc8e4ff };

    static void kneeOf(float X, float Y, float fx, float fy, float cx, float cy, out float kx, out float ky)
    {
      const float a = 10, b = 13;
      float dx = fx - X, dy = fy - Y, d = hypotf(dx, dy);
      d = clampf(d, fabsf(a - b) + 0.01f, a + b - 0.01f);
      float an = atan2f(dy, dx), off = acosf(clampf((a * a + d * d - b * b) / (2 * a * d), -1, 1));
      float bd = -1;
      kx = X;
      ky = Y;
      foreach (int s in new[] { 1, -1 })
      {
        float k = an + off * s, x = X + cosf(k) * a, y = Y + sinf(k) * a, dd = (x - cx) * (x - cx) + (y - cy) * (y - cy);
        if (dd > bd)
        {
          bd = dd;
          kx = x;
          ky = y;
        }
      }
    }

    static void drawTank(float cx, float cy, float[] fxs, float[] fys, float[] lift, float ang, float rec, int t, bool shield)
    {
      // 影
      Ell(cx + 3, cy + 5, 17, 16, 0, 92);
      // 脚: 付け根 → ひざ (油圧の太い腕) → 足 (細い脚と 3 本の爪)
      for (int i = 0; i < 6; i++)
      {
        float X = cx + HIPS[i, 0], Y = cy + HIPS[i, 1], lf = lift[i];
        float fx = fxs[i] - lf * 1.5f, fy = fys[i] - lf * 3;
        float kx, ky;
        kneeOf(X, Y, fx, fy, cx, cy, out kx, out ky);
        kx += (kx - cx) * 0.12f * lf;
        ky -= 2 * lf;
        if (lf > 0.05f)
          Ell(fxs[i] + 1.5f, fys[i] + 2.5f, 2.4f, 1.6f, 0, 80);
        float sx = fx - cx >= 0 ? 1 : -1;
        // 足の爪 (床をつかむ 3 本)
        uint claw = 0x141516;
        Seg(fx, fy, fx - 2.2f, fy + 2.0f, 0.9f, claw);
        Seg(fx, fy, fx + 2.2f, fy + 2.0f, 0.9f, claw);
        Seg(fx, fy, fx + sx * 0.6f, fy + 3.2f, 0.9f, claw);
        Q(fx - 2.0f, fy + 1.6f, 0x4a4e52);
        Q(fx + 1.8f, fy + 1.6f, 0x4a4e52);
        Rod(kx, ky, fx, fy, 1.15f, DSTEEL, 0.35f);
        Disc(fx, fy, 1.3f, 0x24272a);
        Q(fx - 0.6f, fy - 0.7f, lf > 0.05f ? 0xb3b8bcu : 0x6b7075u);
        // 太い腕と、それに沿う油圧の筒
        Rod(X, Y, kx, ky, 1.75f, STEEL, 0.5f);
        float ux = kx - X, uy = ky - Y, l = Math.Max(0.1f, hypotf(ux, uy));
        float nx = -uy / l, ny = ux / l;
        if (nx * (X - cx) + ny * (Y - cy) < 0)
        {
          nx = -nx;
          ny = -ny;
        }
        Seg(X + nx * 1.9f + ux * 0.15f, Y + ny * 1.9f + uy * 0.15f, X + nx * 1.6f + ux * 0.75f, Y + ny * 1.6f + uy * 0.75f, 0.7f, 0xc8ced3);
        Line1(X + nx * 1.6f + ux * 0.15f, Y + ny * 1.6f + uy * 0.15f, X + nx * 1.4f + ux * 0.75f, Y + ny * 1.4f + uy * 0.75f, 0x3a3e42);
        // ひざの関節と、足を上げているときに光る灯
        Ball(kx, ky, 2.3f, STEEL, 0.7f);
        Disc(kx, ky, 0.9f, 0x2a2c2e);
        Disc(kx - 0.15f, ky - 0.15f, 0.55f, lf > 0.05f ? 0xffb23eu : 0x6b7075u);
        if (lf > 0.05f)
          Disc(kx, ky, 2.0f, 0xffb23e, 50);
      }
      // 脇の装甲 (左右の箱)
      for (int s = -1; s <= 1; s += 2)
      {
        float x0 = s < 0 ? cx - 14.5f : cx + 8.8f;
        Plate(x0, cy - 10.5f, 5.7f, 21, STEEL, true, 0.55f, 0.35f);
        for (int k = 0; k < 6; k++) // 冷却のひだ
        {
          float yy = cy - 8 + k * 3.2f;
          Rect(x0 + 0.9f, yy, 3.9f, PX, 0x24272a);
          Rect(x0 + 0.9f, yy + PX, 3.9f, PX, 0x8a9198);
        }
        Q(x0 + 0.7f, cy - 9.8f, 0xdadee2);
        Q(x0 + 4.6f, cy - 9.8f, 0xdadee2);
        Q(x0 + 0.7f, cy + 9.3f, 0x8a9198);
        Q(x0 + 4.6f, cy + 9.3f, 0x8a9198);
      }
      // 車体: 前がとがった六角の甲板
      Poly(0x121416, 255, cx, cy - 19.6f, cx + 9.7f, cy - 13.4f, cx + 9.7f, cy + 12.6f, cx, cy + 16.8f, cx - 9.7f, cy + 12.6f, cx - 9.7f, cy - 13.4f);
      Poly(0x7c838a, 255, cx, cy - 18.8f, cx + 9, cy - 13, cx + 9, cy + 12.2f, cx, cy + 16, cx - 9, cy + 12.2f, cx - 9, cy - 13);
      // 面ごとの明るさ (左が明るい)
      Poly(0x9aa1a8, 255, cx, cy - 18.8f, cx, cy - 12, cx - 9, cy - 12, cx - 9, cy - 13);
      Poly(0x5c6369, 255, cx, cy - 18.8f, cx + 9, cy - 13, cx + 9, cy - 12, cx, cy - 12);
      Poly(0x50565c, 255, cx - 9, cy + 12.2f, cx + 9, cy + 12.2f, cx, cy + 16);
      Rect(cx - 9, cy - 12, 1.2f, 24.2f, 0xbcc2c8);
      Rect(cx + 7.8f, cy - 12, 1.2f, 24.2f, 0x454b51);
      Seg(cx - 8.8f, cy - 13, cx - 0.2f, cy - 18.6f, 0.5f, 0xeef1f2);
      // 甲板の継ぎ目とリベット
      Rect(cx - 7.6f, cy - 9.8f, 15.2f, PX, 0x3e444a);
      Rect(cx - 7.6f, cy + 4.6f, 15.2f, PX, 0x3e444a);
      Rect(cx - 7.6f, cy - 9.8f + PX, 15.2f, PX, 0xa8afb6);
      for (int k = 0; k < 5; k++)
      {
        float rx = cx - 6.6f + k * 3.3f;
        Disc(rx, cy - 11.1f, 0.42f, 0x2e3236);
        Q(rx - 0.25f, cy - 11.4f, 0xe0e4e8);
      }
      // 機関の網 (後ろ) と排気の灯
      Rect(cx - 6.3f, cy + 5.6f, 12.6f, 5.6f, 0x24272a);
      for (int k = 0; k < 7; k++)
      {
        Rect(cx - 6, cy + 6 + k * 0.8f, 12, PX, 0x50565c);
      }
      float heat = 0.5f + 0.5f * sinf(t * 0.6f);
      uint ex = Mix(0x7a2a12, 0xff9a3c, heat);
      for (int s = -1; s <= 1; s += 2)
      {
        Rect(cx + s * 4.5f - 1.6f, cy + 13, 3.2f, 1.9f, 0x1a1b1c);
        Rect(cx + s * 4.5f - 1.2f, cy + 13.3f, 2.4f, 1.2f, ex);
        Disc(cx + s * 4.5f, cy + 14, 2.4f, 0xff7a2a, (int)(40 + 40 * heat));
      }
      // 前の灯
      for (int s = -1; s <= 1; s += 2)
      {
        Disc(cx + s * 3.0f, cy - 8.6f, 0.8f, 0x3a2a10);
        Disc(cx + s * 3.0f, cy - 8.7f, 0.5f, 0xffd27a);
        Disc(cx + s * 3.0f, cy - 9.5f, 1.8f, 0xffc860, 40);
      }
      // 砲身 (2 本。撃つと下がる)
      float dx = cosf(ang), dy = sinf(ang), px = -dy, py = dx, tyy = cy - 1;
      foreach (float s in new[] { -2.5f, 2.5f })
      {
        float bx = cx + px * s, by = tyy + py * s, len = 16 - rec;
        Rod(bx + dx * 4, by + dy * 4, bx + dx * (len - 1.2f), by + dy * (len - 1.2f), 0.95f, GUN, 0.6f);
        Rod(bx + dx * (len - 2.4f), by + dy * (len - 2.4f), bx + dx * len, by + dy * len, 1.25f, GUN, 0.6f); // 先の制退器
        Disc(bx + dx * (len + 0.1f), by + dy * (len + 0.1f), 0.55f, 0x050506);
        if (rec > 1.8f) // 撃った瞬間の火
        {
          Disc(bx + dx * (len + 1.5f), by + dy * (len + 1.5f), 1.6f, 0x9ad0ff, 160);
          Disc(bx + dx * (len + 1.2f), by + dy * (len + 1.2f), 0.8f, 0xffffff);
        }
      }
      // 砲塔
      Ell(cx + 1.5f, tyy + 2, 7.6f, 7.0f, 0, 70);
      Ball(cx, tyy, 7.3f, STEEL, 0.6f, true, 0.2f);
      Ring(cx, tyy, 5.2f, PX, 0x4a5056); // 回る輪の継ぎ目
      // 砲の付け根 (前の箱)
      float mx = cx + dx * 4.6f, my = tyy + dy * 4.6f;
      Poly(0x3a3e42, 255, mx + px * 4 - dx * 1.5f, my + py * 4 - dy * 1.5f, mx + px * 4 + dx * 1.6f, my + py * 4 + dy * 1.6f, mx - px * 4 + dx * 1.6f, my - py * 4 + dy * 1.6f, mx - px * 4 - dx * 1.5f, my - py * 4 - dy * 1.5f);
      Poly(0x6a7178, 255, mx + px * 3.4f - dx * 1.0f, my + py * 3.4f - dy * 1.0f, mx + px * 3.4f + dx * 1.1f, my + py * 3.4f + dy * 1.1f, mx - px * 3.4f + dx * 1.1f, my - py * 3.4f + dy * 1.1f, mx - px * 3.4f - dx * 1.0f, my - py * 3.4f - dy * 1.0f);
      // ハッチ
      Disc(cx + 1.6f, tyy + 2.2f, 2.5f, 0x3a3e42);
      Disc(cx + 1.4f, tyy + 2.0f, 2.0f, 0x6a7178);
      Q(cx + 0.6f, tyy + 1.0f, 0xc4cacf);
      Rect(cx + 0.3f, tyy + 1.9f, 2.4f, PX, 0x2e3236);
      // 潜望鏡と、味方の青い目
      Rect(cx - 3.6f, tyy - 2.2f, 1.6f, 2.4f, 0x2e3236);
      Rect(cx - 3.3f, tyy - 2.0f, 1.0f, 0.9f, 0x6ab8ff);
      float ex2 = cx + dx * 5.4f, ey2 = tyy + dy * 5.4f;
      Disc(ex2, ey2, 2.4f, 0x3a7bff, 60);
      Disc(ex2, ey2, 1.2f, 0x163a78);
      Disc(ex2, ey2, 0.85f, 0x6ab8ff);
      Q(ex2 - 0.4f, ey2 - 0.4f, 0xe8f4ff);
      if (shield) // 入り直したあとの守り (青い輪)
        Ring(cx, cy, 21 + sinf(t * 0.5f), 0.5f, 0x6ab8ff, 90);
    }

    // 確かめる用: 敵・弾・爆発 (page 0) とボス (page 1〜3) を並べて描く
    public void DrawGallery(int[] screen, int page, int frame)
    {
      t = frame;
      mode = Mode.Play;
      en.Clear();
      eb.Clear();
      fx.Clear();
      items.Clear();
      shots.Clear();
      boss = new Boss();
      float sc = SC;
      Func<int, float, float, Enemy> put = (k, x, y) =>
      {
        Enemy e = new Enemy();
        e.k = k;
        e.id = en.Count + 1;
        e.x = e.gx = x;
        e.y = y;
        e.gy = y - sc;
        e.hp = e.maxhp = 10;
        e.t0 = frame - 30;
        en.Add(e);
        return e;
      };
      if (page == 0)
      {
        put(TWIN, 40, 40);
        Enemy tw = put(TWIN, 100, 40);
        tw.st = 1;
        tw.hp = 2;
        put(WALKER, 160, 40);
        Enemy wk = put(WALKER, 210, 40);
        wk.lvl = 2;
        Enemy bg = put(BUG, 30, 90);
        bg.a = 0.5f;
        Enemy bg2 = put(BUG, 60, 90);
        bg2.a = -2.2f;
        Enemy wd = put(WELDER, 110, 100);
        wd.a = 1.2f;
        wd.tm = 3;
        Enemy wd2 = put(WELDER, 180, 100);
        wd2.st = 1;
        put(FORK, 40, 150);
        Enemy fk = put(FORK, 100, 150);
        fk.st = 3;
        fk.cnt = 2;
        Enemy ey = put(EYE, 170, 150);
        ey.a = 1.9f;
        ey.st = 1;
        Enemy sn = put(SNAKE, 200, 175);
        sn.gx = 150;
        sn.gy = 190 - sc;
        sn.ux = 230;
        sn.uy = 200 - sc;
        sn.r = 0.55f;
        for (int i = 0; i < 8; i++)
          sn.seg[i] = i == 3 ? 1 : 2;
        snakeAt(sn, sn.r, sc, out sn.x, out sn.y);
        Enemy sp = put(SCRAP, 40, 230);
        sp.r = 14;
        sp.a = 1;
        Enemy mt = put(MAINT, 100, 225);
        mt.cnt = 5;
        Enemy cr = put(CORE, 140, 230);
        cr.vx = 1.2f;
        cr.vy = -0.8f;
        Enemy vi = put(WALKER, 190, 230);
        vi.virus = 200;
        foreach (float d in new[] { 0f, 1, 2, 3 })
          eb.Add(new Shot(20 + d * 14, 285, 1, 1.5f, (int)d, 9));
        shots.Add(new Shot(90, 285, 0, -7));
        shots.Add(new Shot(100, 285, 1.5f, -6.8f));
        Item it = new Item();
        it.x = 125;
        it.y = 285;
        it.t = frame;
        items.Add(it);
        fx.Add(new Fx(0, 160, 280, 1, 6, 26, false));
        fx.Add(new Fx(5, 190, 280, 1, 4, 16, false));
        fx.Add(new Fx(6, 220, 280, 1, 5, 14, false));
        fx.Add(new Fx(2, 160, 305, 1, 10, 40, false));
        fx.Add(new Fx(3, 210, 305, 1, 2, 5, false));
        fx.Add(new Fx(4, 230, 305, 1, 4, 16, false));
        tankOn = false;
      }
      else
      {
        boss.k = page - 1;
        boss.x = W / 2;
        boss.ty = boss.y = page == 1 ? 56 : page == 2 ? 120 : 120;
        boss.t = frame;
        for (int i = 0; i < 9; i++)
          boss.hp[i] = boss.maxhp[i] = 10;
        if (page == 1)
        {
          boss.hookY = 220;
          boss.elev = 30;
          boss.hookOff = 60;
        }
        if (page == 2)
        {
          boss.elev = 20;
          boss.hp[2] = 0;
        }
        if (page == 3)
          boss.hp[3] = 0;
        tankOn = true;
        tx = 120;
        ty = 280;
        initFeet();
      }

      DrawFrame(screen);
    }

    // 窓のアイコンの絵 (192x192、まわりは透明)
    public const int ICON = 192;
    public static int[] IconArt()
    {
      int[] buf = new int[ICON * ICON];
      Surf s = new Surf();
      s.Buf = buf;
      s.Stride = ICON;
      s.Hpx = ICON;
      s.CX1 = ICON - 1;
      s.CY0 = 0;
      s.CY1 = ICON - 1;
      Surf old = Sf;
      Sf = s;
      // 角の丸い鉄の板と、下の黄色と黒のすじ
      const float U = ICON / (float)S;
      Disc(6, 6, 5, 0x2a2c2e);
      Disc(U - 6, 6, 5, 0x2a2c2e);
      Disc(6, U - 6, 5, 0x2a2c2e);
      Disc(U - 6, U - 6, 5, 0x2a2c2e);
      Rect(1, 6, U - 2, U - 12, 0x2a2c2e);
      Rect(6, 1, U - 12, U - 2, 0x2a2c2e);
      Rect(3, 3, U - 6, U - 6, 0x3e4246);
      for (float x = -8; x < U; x += 6)
        Poly(0xc9a227, 255, x, U - 3, x + 3, U - 3, x + 6, U - 8, x + 3, U - 8);
      Rect(3, U - 9, U - 6, 1, 0x1a1b1c);
      float cx = U / 2, cy = U / 2 + 1;
      float[] fxs = new float[6], fys = new float[6], lift = new float[6];
      for (int i = 0; i < 6; i++)
      {
        fxs[i] = cx + REST[i, 0] * 0.9f;
        fys[i] = cy + REST[i, 1] * 0.9f;
      }
      drawTank(cx, cy, fxs, fys, lift, -PIf / 2, 0, 0, false);
      Sf = old;
      return buf;
    }

    // ================= 弾・爆発・部品 =================
    // 自分の弾: 進む向きに伸びた青い光の筋
    static void drawShot(float x, float y, float ux, float uy, int t)
    {
      Seg(x - ux * 5.5f, y - uy * 5.5f, x + ux * 4.5f, y + uy * 4.5f, 4.6f, 0x468cff, 55);
      Seg(x - ux * 4.5f, y - uy * 4.5f, x + ux * 4f, y + uy * 4f, 2.5f, 0x3a7bff);
      Seg(x - ux * 3.5f, y - uy * 3.5f, x + ux * 3.6f, y + uy * 3.6f, 1.2f, 0x9ad0ff);
      Seg(x - ux * 2f, y - uy * 2f, x + ux * 3.4f, y + uy * 3.4f, 0.6f, 0xffffff);
    }

    static void drawBomb(float bx, float by, float gy, int t)
    {
      Ell(bx + 1, gy + 2, 3.4f, 2.2f, 0, 90);
      Ball(bx, by, 3.8f, GUN, 0.8f);
      Ring(bx, by, 2.4f, PX, 0x5a6066);
      Rect(bx - 0.6f, by - 4.6f, 1.2f, 1.2f, 0x3a3e42); // 信管
      bool on = ((t >> 1) & 1) != 0;
      Disc(bx + 1.2f, by + 1.2f, 0.8f, on ? 0x6ab8ffu : 0x3a7bffu);
      if (on)
        Disc(bx + 1.2f, by + 1.2f, 2.2f, 0x6ab8ff, 70);
    }

    // 弾を強くする部品: 青く光るエネルギーの電池 (味方の色)。脈打つ光の輪と P の字
    static void drawItem(float x, float y, int t)
    {
      float p = 0.5f + 0.5f * sinf(t * 0.25f);
      Disc(x, y, 12, 0x468cff, 20 + (int)(30 * p));
      Ring(x, y, 9.5f + p * 2, 0.7f, 0x6ab8ff, 150);
      Plate(x - 6.2f, y - 8.2f, 12.4f, 16.4f, STEEL, true, 0.6f, 0.4f);
      Plate(x - 2.2f, y - 10.4f, 4.4f, 2.4f, STEEL, true, 0.7f, 0.5f); // 電池の頭
      // 中の光る液
      for (int k = 0; k < 3; k++)
      {
        float yy = y - 6 + k * 4.1f;
        Rect(x - 4.6f, yy, 9.2f, 3.6f, k == 0 ? 0x2a64c8u : 0x163a78u);
        float lv = clampf(p * 1.2f - k * 0.1f, 0, 1);
        Rect(x - 4.6f, yy + 3.6f * (1 - lv), 9.2f, 3.6f * lv, p > 0.5f ? 0x6ab8ffu : 0x3a7bffu);
        Rect(x - 4.6f, yy, 9.2f, PX, 0x9ad0ff);
      }
      Rect(x - 4.4f, y - 6, PX * 2, 12, 0xe0f0ff, 120);
      // P の字
      uint wc = 0xffffff;
      float s = 0.7f;
      Rect(x - 2.2f, y - 4.2f, s, 8.4f, wc);
      Rect(x - 2.2f, y - 4.2f, 3.6f, s, wc);
      Rect(x - 2.2f, y - 0.6f, 3.6f, s, wc);
      Rect(x + 1.4f, y - 3.6f, s, 3.0f, wc);
      Q(x + 1.4f, y - 4.2f, 0x9ad0ff);
      Q(x + 1.4f, y - 0.6f, 0x9ad0ff);
    }

    // 炎の玉 (ふちがでこぼこの、何色かの層)
    static void fireBall(float x, float y, float r, float s, float q)
    {
      if (r <= 0.3f)
        return;
      uint[] cols = { 0xc2410c, 0xff7a1f, 0xffc23e, 0xfff6d0 };
      float[] ks = { 1.0f, 0.78f, 0.52f, 0.26f };
      for (int L = 0; L < 4; L++)
      {
        float rr = r * ks[L];
        float ox = -L * r * 0.07f, oy = -L * r * 0.1f;
        Disc(x + ox, y + oy, rr, cols[L]);
        if (L < 2)
          for (int k = 0; k < 6; k++) // でこぼこ
          {
            float a = k * 1.047f + s + L * 0.5f + q * 2, d = rr * (0.62f + 0.2f * hs(s + k + L * 7));
            Disc(x + ox + cosf(a) * d, y + oy + sinf(a) * d, rr * (0.42f + 0.15f * hs(s * 3 + k)), cols[L]);
          }
      }
    }
    static void smoke(float x, float y, float r, int a, float s)
    {
      for (int k = 0; k < 4; k++)
      {
        float an = k * 1.57f + s, d = r * 0.45f;
        Disc(x + cosf(an) * d, y + sinf(an) * d * 0.8f, r * 0.62f, 0x2e2a26, a);
      }
      Disc(x - r * 0.2f, y - r * 0.25f, r * 0.5f, 0x4a4540, a * 2 / 3);
    }

    static void drawFx(int k, float x, float y, float s, int t, int life)
    {
      float q = (float)t / life;
      if (k == 0)
      {
        if (q > 0.35f)
          smoke(x + 2, y - q * 10, 4 + q * 10, (int)(130 * (1 - q)), s);
        Disc(x, y, 5 + q * 13, 0xff8c28, (int)(90 * (1 - q)));
        if (q < 0.55f)
          fireBall(x, y, (3 + 9 * (1 - q / 0.55f)) * 1.05f, s, q);
        for (int i = 0; i < 12; i++) // 火の粉と破片
        {
          float an = i * 0.52f + s, d = 3 + q * 22 * (0.6f + hs(i + s) * 0.6f);
          float px = x + cosf(an) * d, py = y + sinf(an) * d;
          Seg(px - cosf(an) * 1.4f, py - sinf(an) * 1.4f, px, py, 0.6f, i % 3 != 0 ? 0xffd25au : 0xff5a1fu);
          if (i % 4 == 0)
          {
            Poly(0x2a2420, 255, px * 0.7f + x * 0.3f - 1, py * 0.7f + y * 0.3f, px * 0.7f + x * 0.3f + 0.4f, py * 0.7f + y * 0.3f - 1.2f, px * 0.7f + x * 0.3f + 1.3f, py * 0.7f + y * 0.3f + 0.5f);
          }
        }
      }
      else if (k == 3) // 弾をはじいた火花
      {
        for (int i = 0; i < 6; i++)
        {
          float an = i * 1.05f + s, d = 2 + t * 1.5f;
          Seg(x + cosf(an) * (d - 1.5f), y + sinf(an) * (d - 1.5f), x + cosf(an) * d, y + sinf(an) * d, 0.5f, i % 2 != 0 ? 0xffe27au : 0xffffffu);
        }
        if (t < 2)
          Disc(x, y, 1.6f, 0xffffff, 200);
      }
      else if (k == 6) // 爆弾の爆風の輪
      {
        float r = 6 + q * 34;
        Ring(x, y, r, 1.4f, 0xffd25a, (int)(220 * (1 - q)));
        Ring(x, y, r - 1.8f, 1.2f, 0xff8c28, (int)(150 * (1 - q)));
        Disc(x, y, r * 0.8f, 0xff8c28, (int)(45 * (1 - q)));
        for (int i = 0; i < 16; i++) // 青いウイルスの火花
        {
          float an = i * 0.39f + q * 2;
          Q(x + cosf(an) * r * 0.9f, y + sinf(an) * r * 0.9f, i % 2 == 0 ? 0x6ab8ffu : 0xe0f0ffu);
        }
      }
      else if (k == 4) // 核が乗り移った: 赤い輪が縮む
      {
        float r = 16 * (1 - q) + 3;
        Ring(x, y, r, 0.8f, Ep.RED, (int)(220 * (1 - q * 0.5f)));
        Ring(x, y, r * 0.6f, 0.6f, Ep.RED, (int)(140 * (1 - q)));
        Disc(x, y, r * 0.3f, Ep.RED, (int)(60 * (1 - q)));
      }
      else // 5: 小さい爆発 (虫や節)
      {
        Disc(x, y, 3 + q * 7, 0xff8c28, (int)(110 * (1 - q)));
        if (q < 0.5f)
          fireBall(x, y, 4 * (1 - q * 2) + 1, s, q);
        for (int i = 0; i < 6; i++)
        {
          float an = i * 1.05f + s, d = 2 + q * 12;
          Disc(x + cosf(an) * d, y + sinf(an) * d, 0.5f, i % 2 != 0 ? 0xffd25au : 0x55585bu);
        }
      }
    }

    // 戦車がやられたときの大きな爆発 (脚の破片が飛ぶ)
    static void drawBigBoom(float x, float y, float s, int t, int life)
    {
      float q = (float)t / life;
      Disc(x, y, 8 + q * 26, 0xff8c28, (int)(100 * (1 - q)));
      if (q < 0.5f)
        fireBall(x, y, 6 + 16 * (1 - q / 0.5f), s, q);
      if (q > 0.3f)
        for (int i = 0; i < 3; i++)
          smoke(x + (i - 1) * 8, y - q * 24 - i * 3, 5 + q * 12, (int)(140 * (1 - q)), s + i);
      for (int i = 0; i < 6; i++) // 脚の破片
      {
        float an = i * 1.047f + s, d = 6 + q * 40;
        float px = x + cosf(an) * d, py = y + sinf(an) * d * 0.8f;
        Rod(px, py, px + cosf(an + q * 6) * 5, py + sinf(an + q * 6) * 5, 1.0f, DSTEEL, 0.4f);
      }
      for (int i = 0; i < 16; i++)
      {
        float an = i * 0.39f + s, d = 4 + q * 34 * (0.5f + hs(i + s) * 0.7f);
        float px = x + cosf(an) * d, py = y + sinf(an) * d;
        Seg(px - cosf(an) * 1.6f, py - sinf(an) * 1.6f, px, py, 0.6f, i % 3 != 0 ? 0xffd25au : 0xff5a1fu);
      }
    }

    // ================= 文字と枠 =================
    const uint INK = 0x000000, CREAM = 0xF2F0E8, GOLD = 0xC9A227, REDT = 0xFF6A5A, MUTE = 0x9A958A;
    static void txt(float x, float y, uint c, string s, bool center = false, int size = 1) { Txt.Draw(x, y, c, s, center, size, INK); }
    static void panel(float x, float y, float w, float h)
    {
      R(x, y, w, h, 0x0c0b0a, 200);
      Rect(x + 1, y + 1, w - 2, h - 2, 0x2a1a10, 40);
      R(x, y, w, 1, 0x8a4f28);
      R(x, y + h - 1, w, 1, 0x8a4f28);
      R(x, y, 1, h, 0x6b3a1e);
      R(x + w - 1, y, 1, h, 0x6b3a1e);
      Rect(x, y, w, PX, 0xd09a6a);
      Rect(x, y, PX, h, 0xa8683a);
      for (float i = 0; i < w - 2; i += 8) // 上の縁に黄色と黒のすじ
      {
        Poly(GOLD, 255, x + 1 + i, y + 4, x + 1 + i + 2, y + 2, x + 1 + i + 5, y + 2, x + 1 + i + 3, y + 4);
      }
      foreach (float bx in new[] { x + 2.5f, x + w - 2.5f }) // 角のボルト
        foreach (float by in new[] { y + 2.5f, y + h - 2.5f })
        {
          Disc(bx, by, 0.9f, 0x4a2a1a);
          Q(bx - 0.4f, by - 0.4f, 0xd09a6a);
        }
    }
    static void lifeIcon(float x, float y)
    {
      for (int s = -1; s <= 1; s += 2)
      {
        Seg(x + s * 2, y + 1, x + s * 5, y - 2, 0.7f, 0x6b7075);
        Seg(x + s * 2, y + 4, x + s * 5, y + 7, 0.7f, 0x6b7075);
        Seg(x + s * 3, y + 2.5f, x + s * 5.5f, y + 2.5f, 0.7f, 0x6b7075);
      }
      Plate(x - 2.6f, y - 1.6f, 5.2f, 8.6f, STEEL, true, 0.7f, 0.45f);
      Disc(x, y + 2.4f, 1.3f, 0x3a7bff);
      Q(x - 0.4f, y + 2.0f, 0xe0f0ff);
    }

    void drawOverlay()
    {
      bool playing = mode == Mode.Play || mode == Mode.Continue;
      // 上の点数
      if (mode != Mode.Attract)
      {
        if (!playing || (t / 16) % 2 == 1)
          txt(4, 2, GOLD, "1UP");
        txt(4, 11, CREAM, Score.ToString("0000000"));
      }
      txt(W / 2 + 8, 2, GOLD, "HI-SCORE", true);
      txt(W / 2 + 8, 11, CREAM, Math.Max(Score, Table[0].Score).ToString("0000000"), true);
      if (mode != Mode.Attract)
      {
        string zn = Bg.ZONES[Zone].Name;
        txt(W - 4 - zn.Length * 6, 2, GOLD, zn);
        string s = "ZONE " + (Zone + 1);
        txt(W - 4 - s.Length * 6, 11, MUTE, s);
      }
      // 残機と弾の強さ (右下の青い目盛り)
      if (playing)
      {
        for (int i = 0; i < Math.Min(lives, 8); i++)
          lifeIcon(10 + i * 13, 308);
        for (int i = 0; i < 4; i++)
        {
          Plate(W - 54.5f + i * 12, 304.5f, 10, 7, DSTEEL, true, 0.3f, 0.2f);
          if (i <= shotLv)
          {
            bool hot = i == shotLv && shotLv == 3 && (t & 4) != 0;
            Rect(W - 53 + i * 12, 306, 7, 4, hot ? 0xe0f0ffu : 0x3a7bffu);
            Rect(W - 53 + i * 12, 306, 7, PX * 2, 0x9ad0ff);
          }
        }
      }
      // ボスの耐久の帯と、出てきたとき・倒したときの知らせ
      if (boss.k >= 0 && boss.dieT == 0 && mode != Mode.Attract)
      {
        txt(4, 22, REDT, bossName(boss.k));
        Rect(51.5f, 23.5f, 183, 6, 0x000000);
        R(52, 24, 182, 5, 0x1a0806);
        float w = 182f * boss.hp[0] / Math.Max(1, boss.maxhp[0]);
        uint bc = boss.stun > 0 ? 0x3a7bffu : 0xff3b2au;
        R(52, 24, w, 5, bc);
        Rect(52, 24, w, PX * 2, Mix(bc, 0xffffff, 0.45f));
        Rect(52, 28, w, PX * 2, Mul(bc, 0.6f));
        for (float x = 52 + 182f / 10; x < 234; x += 182f / 10) // 目盛り
          Rect(x, 24, PX, 5, 0x000000, 120);
      }
      if (bossBannerT > 0 && mode == Mode.Play && !Paused)
      {
        if (bossResult == 0 && (bossBannerT / 8) % 2 == 1)
        {
          for (int k = 0; k < 2; k++) // 黄色と黒の帯
            for (float x = -((t * 0.5f) % 10); x < W; x += 10)
              Poly(k == 0 ? 0xc9a227u : 0xc9a227u, 200, x, 112 + k * 46, x + 5, 112 + k * 46, x + 2, 116 + k * 46, x - 3, 116 + k * 46);
          txt(W / 2, 120, REDT, "WARNING", true, 5);
          txt(W / 2, 150, CREAM, bossName(boss.k), true, 3);
        }
        else if (bossResult == 1)
          txt(W / 2, 130, GOLD, "DESTROYED", true, 5);
        else if (bossResult == 2)
          txt(W / 2, 130, MUTE, "IT ESCAPED", true, 3);
      }

      switch (mode)
      {
        case Mode.Attract:
          if ((modeT / (8 * FPS)) % 2 == 0)
          {
            panel(20, 70, 200, 158);
            txt(W / 2, 84, GOLD, "FOUNDRY", true, 5);
            txt(W / 2, 114, CREAM, "HEXAPOD TANK", true);
            txt(W / 2, 128, MUTE, "THE MACHINES WANT TO LIVE", true);
            if ((modeT / 15) % 2 == 0)
              txt(W / 2, 152, CREAM, "PRESS START", true, 3);
            txt(W / 2, 182, MUTE, "Z: SHOT  X: BOMB", true);
            txt(W / 2, 194, MUTE, "SHIFT: SLOW  ESC: PAUSE", true);
            txt(W / 2, 210, MUTE, "F11: FULL SCREEN", true);
          }
          else
          {
            panel(30, 70, 180, 150);
            txt(W / 2, 82, GOLD, "BEST 5", true, 3);
            drawTable(false);
          }
          break;
        case Mode.Play:
          if (bannerT > 0 && bannerT < 3 * FPS - 6)
          {
            panel(30, 120, 180, 44);
            txt(W / 2, 128, GOLD, "ZONE " + (bannerZone + 1), true);
            txt(W / 2, 142, CREAM, Bg.ZONES[bannerZone].Name, true, 3);
          }
          if (extendT > 0 && (extendT / 6) % 2 == 1)
            txt(W / 2, 180, GOLD, "EXTEND!", true, 3);
          if (powT > 0 && (powT / 4) % 2 == 1)
            txt(W / 2, 200, 0x6ab8ff, shotLv >= 3 ? "MAX POWER" : "POWER UP", true, 3);
          if (Paused)
          {
            panel(40, 120, 160, 64);
            txt(W / 2, 130, CREAM, "PAUSE", true, 3);
            txt(W / 2, 156, MUTE, "ESC: RESUME", true);
            txt(W / 2, 168, MUTE, "Q: QUIT", true);
          }
          break;
        case Mode.Continue:
          panel(40, 110, 160, 90);
          txt(W / 2, 120, CREAM, "CONTINUE?", true, 3);
          txt(W / 2, 144, GOLD, (modeT / FPS).ToString(), true, 5);
          txt(W / 2, 182, MUTE, "Z: CONTINUE", true);
          break;
        case Mode.GameOver:
          txt(W / 2, 140, REDT, "GAME OVER", true, 5);
          break;
        case Mode.NameEntry:
          {
            panel(30, 90, 180, 120);
            txt(W / 2, 100, GOLD, "NEW RECORD", true, 3);
            txt(W / 2, 124, CREAM, "SCORE " + Score.ToString("0000000"), true);
            for (int i = 0; i < 3; i++)
            {
              float x = W / 2 - 22 + i * 22;
              txt(x, 144, i == entryPos ? GOLD : CREAM, entry[i].ToString(), true, 5);
              if (i == entryPos && (t / 8) % 2 == 1)
                R(x - 8, 171, 16, 2, GOLD);
            }
            txt(W / 2, 186, MUTE, "UP/DOWN: LETTER  Z: OK", true);
            txt(200, 98, MUTE, (modeT / FPS).ToString());
            break;
          }
        case Mode.Ranking:
          {
            panel(30, 70, 180, 160);
            txt(W / 2, 82, GOLD, "BEST 5", true, 3);
            drawTable(true);
            txt(W / 2, 214, MUTE, "YOUR SCORE " + Score.ToString("0000000"), true);
            break;
          }
      }
    }

    static readonly string[] ORD = { "1ST", "2ND", "3RD", "4TH", "5TH" };
    void drawTable(bool mark)
    {
      for (int i = 0; i < 5; i++)
      {
        string s = ORD[i] + " " + Table[i].Score.ToString("0000000") + " " + Table[i].Name.PadRight(3);
        bool mine = mark && i == newRank;
        if (!mine || (t / 8) % 2 == 1)
          txt(W / 2, 110 + i * 20, mine ? REDT : i == 0 ? GOLD : CREAM, s, true);
      }
    }
  }
}
