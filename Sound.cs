// 音。素材のファイルは使わず、全部その場で合成する (ESP32 版 sound.cpp・Windows 版 SkyLark の Sound.cs と同じ音)。
// winmm の waveOut に、裏のスレッドで混ぜた音を流し続ける。鳴らしきりの音は起動時に波形を作っておき、
// 鳴りつづける音 (工場のうなり、溶鉱炉、ボスのうなり) はゲームが毎フレーム音量を決めて、混ぜるときに作る
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Foundry
{
  public enum Sfx
  {
    MomHit,    // 当たった: 金属を打つ音
    BigBoom,
    Crash,     // ボスを倒した
    Death,     // 戦車がやられた
    Power,
    Extend,
    Warning,
    EnemyShot,
    GameOver,
    Tick,
    Select,
    Pause,
    Growl,     // フォークがふかす
    Bomb,      // 爆弾を投げる
    Flak,      // 小さな爆発
    G2Shot,    // 戦車の青い弾: トゥン
    G2Boom,    // 機械が壊れる: 爆発に金属のガシャン
    CoreFree,  // 核が飛び出す: 上ずる電子音
    CoreKill,  // 核を撃ち落とした: ガラスのようなチン
    Possess,   // 核が乗り移った: 下がっていく不気味なうなり
    Virus,     // ウイルス: デジタルのノイズ
    Ping,      // 弾をはじいた: キン
    Alarm,     // 防犯カメラに見つかった: ピピッ
    Steam,     // 蒸気: シュー
    Step,      // 戦車の足が着く: カシャ
    Slam,      // フックやプレスが落ちる: ズシン
    G2Start,   // 出発: 工場らしい短い節
    PileDrive, // 背景: 遠くのくい打ち (ガーン)
    MachClank, // 背景: 遠くの大きな機械 (ガシャン・シュー)
    Count
  }

  public class Sound : IDisposable
  {
    const int SR = 22050;
    const int BUF = 512;  // 1 つの送り枠の長さ (約 23ms)
    const int NBUF = 4;
    const double PI2 = Math.PI * 2;

    // ---- 鳴りつづける音の大きさ (0〜1)。ゲームのスレッドが書き、音のスレッドが読む ----
    public volatile float MomHum, Hum, Roar;
    public volatile float Master = 1;
    public volatile bool Muted;

    readonly float[][] waves = new float[(int)Sfx.Count][];
    readonly float[] waveVol = new float[(int)Sfx.Count];
    class Voice
    {
      public float[] w;
      public int pos;
      public float vol;
    }
    readonly List<Voice> voices = new List<Voice>();
    readonly List<Voice> pending = new List<Voice>();
    readonly object lk = new object();
    readonly Random rng = new Random();

    IntPtr hwo;
    IntPtr[] hdrs = new IntPtr[NBUF];
    IntPtr[] datas = new IntPtr[NBUF];
    int flagsOfs;
    Thread th;
    volatile bool running;
    readonly short[] pcm = new short[BUF];
    readonly float[] mixBuf = new float[BUF];

    // 鳴りつづける音の作りかけの状態
    double momPh, humPh, humPh2, chugPh;
    float humLp, chugLp, roarLp, roarLp2, roarAm = 1, roarAmS = 1;
    int roarN;
    float curMom, curHum, curRoar;

    public Sound()
    {
      MakeWaves();
      try
      {
        Open();
      }
      catch (Exception)
      {
        hwo = IntPtr.Zero; // 音が出せない PC でも遊べるようにする
      }
    }

    public void Play(Sfx s, float vol = 1)
    {
      if (hwo == IntPtr.Zero || Muted)
        return;
      float[] w = waves[(int)s];
      if (w == null)
        return;
      lock (lk)
      {
        // 同じ音を同時にいくつも重ねない (弾が鳴りすぎないように)
        int same = 0;
        foreach (Voice v in voices)
          if (v.w == w && v.pos < SR / 20)
            same++;
        foreach (Voice v in pending)
          if (v.w == w)
            same++;
        if (same >= 2)
          return;
        Voice nv = new Voice();
        nv.w = w;
        nv.vol = vol * waveVol[(int)s];
        pending.Add(nv);
      }
    }

    // ---- waveOut ----
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
      public ushort wFormatTag, nChannels;
      public uint nSamplesPerSec, nAvgBytesPerSec;
      public ushort nBlockAlign, wBitsPerSample, cbSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
      public IntPtr lpData;
      public uint dwBufferLength, dwBytesRecorded;
      public IntPtr dwUser;
      public uint dwFlags, dwLoops;
      public IntPtr lpNext, reserved;
    }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    const uint WHDR_DONE = 1;

    void Open()
    {
      WAVEFORMATEX f = new WAVEFORMATEX();
      f.wFormatTag = 1;
      f.nChannels = 1;
      f.nSamplesPerSec = SR;
      f.wBitsPerSample = 16;
      f.nBlockAlign = 2;
      f.nAvgBytesPerSec = SR * 2;
      if (waveOutOpen(out hwo, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        throw new Exception("waveOutOpen");
      int hs = Marshal.SizeOf(typeof(WAVEHDR));
      flagsOfs = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
      for (int i = 0; i < NBUF; i++)
      {
        datas[i] = Marshal.AllocHGlobal(BUF * 2);
        hdrs[i] = Marshal.AllocHGlobal(hs);
        WAVEHDR h = new WAVEHDR();
        h.lpData = datas[i];
        h.dwBufferLength = BUF * 2;
        Marshal.StructureToPtr(h, hdrs[i], false);
        waveOutPrepareHeader(hwo, hdrs[i], hs);
        Marshal.WriteInt32(hdrs[i], flagsOfs, Marshal.ReadInt32(hdrs[i], flagsOfs) | (int)WHDR_DONE); // 最初は全部空き
      }
      running = true;
      th = new Thread(Loop);
      th.IsBackground = true;
      th.Priority = ThreadPriority.AboveNormal;
      th.Start();
    }

    void Loop()
    {
      int hs = Marshal.SizeOf(typeof(WAVEHDR));
      while (running)
      {
        bool wrote = false;
        for (int i = 0; i < NBUF; i++)
        {
          int fl = Marshal.ReadInt32(hdrs[i], flagsOfs);
          if ((fl & WHDR_DONE) == 0)
            continue;
          Mix();
          Marshal.Copy(pcm, 0, datas[i], BUF);
          Marshal.WriteInt32(hdrs[i], flagsOfs, fl & ~(int)WHDR_DONE);
          waveOutWrite(hwo, hdrs[i], hs);
          wrote = true;
        }
        if (!wrote)
          Thread.Sleep(2);
      }
    }

    public void Dispose()
    {
      if (hwo == IntPtr.Zero)
        return;
      running = false;
      if (th != null)
        th.Join(200);
      waveOutReset(hwo);
      int hs = Marshal.SizeOf(typeof(WAVEHDR));
      for (int i = 0; i < NBUF; i++)
      {
        waveOutUnprepareHeader(hwo, hdrs[i], hs);
        Marshal.FreeHGlobal(hdrs[i]);
        Marshal.FreeHGlobal(datas[i]);
      }
      waveOutClose(hwo);
      hwo = IntPtr.Zero;
    }

    // ---- 混ぜる ----
    void Mix()
    {
      Array.Clear(mixBuf, 0, BUF);
      lock (lk)
      {
        voices.AddRange(pending);
        pending.Clear();
      }
      bool mute = Muted;
      for (int vi = voices.Count - 1; vi >= 0; vi--)
      {
        Voice v = voices[vi];
        int n = Math.Min(BUF, v.w.Length - v.pos);
        if (!mute)
          for (int i = 0; i < n; i++)
            mixBuf[i] += v.w[v.pos + i] * v.vol;
        v.pos += n;
        if (v.pos >= v.w.Length)
          voices.RemoveAt(vi);
      }
      LoopSounds(mute);
      float m = mute ? 0 : Master;
      for (int i = 0; i < BUF; i++)
      {
        float x = mixBuf[i] * m;
        x = x / (1 + Math.Abs(x) * 0.6f); // やわらかく頭を抑える
        pcm[i] = (short)(Math.Max(-1f, Math.Min(1f, x)) * 30000);
      }
    }

    static float Toward(float cur, float target) { return cur + (target - cur) * 0.08f; }
    float Noise() { return (float)(rng.NextDouble() * 2 - 1); }

    void LoopSounds(bool mute)
    {
      // 1 枠ごとに少しずつ目標の音量へ寄せる (急に切れてプツッと鳴らないように)
      curMom = Toward(curMom, mute ? 0 : MomHum);
      curHum = Toward(curHum, mute ? 0 : Hum);
      curRoar = Toward(curRoar, mute ? 0 : Roar);
      for (int i = 0; i < BUF; i++)
      {
        double v = 0;
        if (curMom > 0.001f)
        {
          // ボス: 低くうねる音 (うねりは 24 周に 1 回)
          momPh += 48.0 / SR;
          if (momPh >= 24)
            momPh -= 24;
          double env = 0.6 + 0.4 * Math.Sin(momPh / 24 * PI2);
          v += (Math.Sin(momPh * PI2) + 0.4 * Math.Sin(momPh * 4.02 * PI2)) * env * 0.35 * curMom;
        }
        if (curHum > 0.001f)
        {
          // 工場のうなり: 少しずれた 2 つの低い音のうねりと、こもった空気の音
          humPh += 110.0 / SR;
          humPh2 += 110.6 / SR;
          if (humPh >= 1) humPh -= 1;
          if (humPh2 >= 1) humPh2 -= 1;
          humLp += (Noise() - humLp) * 0.02f;
          // 大きな機関のドッドッドッ (1 秒に 3 回ほど、こもった雑音が脈打つ)
          chugPh += 3.1 / SR;
          if (chugPh >= 1) chugPh -= 1;
          double pulse = 1 - chugPh * 3;
          pulse = pulse > 0 ? pulse * pulse : 0;
          chugLp += (Noise() - chugLp) * 0.06f;
          v += ((Math.Sin(humPh * PI2) + Math.Sin(humPh2 * PI2)) * 0.08 + humLp * 0.5 + chugLp * pulse * 1.6) * curHum;
        }
        if (curRoar > 0.001f)
        {
          // 溶鉱炉: 低い雑音が、炎のようにゆらぐ
          if (++roarN >= SR / 12)
          {
            roarN = 0;
            roarAm = 0.6f + (float)rng.NextDouble() * 0.4f;
          }
          roarLp += (Noise() - roarLp) * 0.05f;
          roarLp2 += (roarLp - roarLp2) * 0.1f;
          roarAmS += (roarAm - roarAmS) * 0.002f; // 急に変えるとプツッと鳴るので、なめらかに寄せる
          v += roarLp2 * 2.2 * roarAmS * curRoar;
        }
        mixBuf[i] += (float)v;
      }
    }

    // ---- 鳴らしきりの音の波形 ----
    float[] New(double sec) { return new float[(int)(SR * sec)]; }
    static float Square(double ph) { return ph - Math.Floor(ph) < 0.5 ? 1f : -1f; }

    // 音符を並べる (矩形波に少し三角を混ぜた、やわらかい笛の音)。notes は半音 (A4 = 0)、len は秒
    float[] Melody(int[] notes, double[] len, double duty)
    {
      double total = 0;
      foreach (double l in len)
        total += l;
      float[] w = New(total + 0.1);
      int p = 0;
      for (int k = 0; k < notes.Length; k++)
      {
        int n = (int)(SR * len[k]);
        double f = 440 * Math.Pow(2, notes[k] / 12.0), ph = 0;
        for (int i = 0; i < n && p + i < w.Length; i++)
        {
          ph += f / SR;
          double fr = ph - Math.Floor(ph);
          float sq = fr < duty ? 1f : -1f, tri = (float)(1 - 4 * Math.Abs(fr - 0.5));
          float env = Math.Min(1, i / (SR * 0.005f)) * (float)Math.Exp(-2.5 * i / n);
          w[p + i] += (sq * 0.35f + tri * 0.65f) * env;
        }
        p += n;
      }
      return w;
    }

    // 爆発: 低いほうへこもっていく雑音と、ドンという低い音
    float[] Explosion(double sec, float lpStart, double thump)
    {
      float[] w = New(sec);
      float lp = 0, lp2 = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR, k = t / sec;
        float cut = (float)(lpStart * 4 * (1 - k) + lpStart * 0.3);
        lp += (Noise() - lp) * Math.Min(0.9f, cut * 3);
        lp2 += (lp - lp2) * Math.Min(0.9f, cut * 3);
        double env = Math.Min(1, t / 0.004) * Math.Pow(1 - k, 2.2);
        w[i] = (float)((lp2 * 2.2 + Math.Sin(t * PI2 * thump * (1 - 0.5 * k)) * 0.6 * Math.Exp(-t * 6)) * env);
      }
      return w;
    }

    void Set(Sfx s, float[] w, float vol)
    {
      waves[(int)s] = w;
      waveVol[(int)s] = vol;
    }

    void MakeWaves()
    {
      float[] w;
      // 当たった: 金属を打つ音
      w = New(0.09);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        w[i] = (float)((Math.Sin(t * PI2 * 1230) + Math.Sin(t * PI2 * 1847) * 0.7) * Math.Exp(-t * 45)) * 0.5f;
      }
      Set(Sfx.MomHit, w, 0.14f);
      Set(Sfx.BigBoom, Explosion(1.1, 0.06f, 50), 0.42f);
      Set(Sfx.Crash, Explosion(3.2, 0.035f, 38), 0.5f);
      // 戦車がやられた: 爆発に下がっていく音を重ねる
      w = Explosion(1.2, 0.08f, 60);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        w[i] += Square(t * (700 - 260 * t)) * (float)Math.Exp(-t * 3) * 0.25f;
      }
      Set(Sfx.Death, w, 0.42f);
      Set(Sfx.Power, Melody(new[] { 3, 7, 10, 15, 19 }, new[] { 0.05, 0.05, 0.05, 0.05, 0.16 }, 0.25), 0.3f);
      Set(Sfx.Extend, Melody(new[] { 3, 7, 10, 7, 10, 15 }, new[] { 0.09, 0.09, 0.09, 0.09, 0.09, 0.35 }, 0.5), 0.35f);
      // 警報: 高い・低いをくり返す
      w = New(2.6);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        double f = (int)(t * 4) % 2 == 0 ? 740 : 554;
        w[i] = (Square(t * f) * 0.5f + (float)Math.Sin(t * f * PI2) * 0.5f) * (float)Math.Min(1, (2.6 - t) * 4) * 0.6f;
      }
      Set(Sfx.Warning, w, 0.3f);
      // 敵の弾: ふわっと下がる小さな音
      w = New(0.12);
      double eph = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        eph += (1400 - 7000 * t) / SR;
        w[i] = (float)Math.Sin(eph * PI2) * (float)Math.Exp(-t * 25);
      }
      Set(Sfx.EnemyShot, w, 0.09f);
      Set(Sfx.GameOver, Melody(new[] { 10, 9, 5, 2, 0, -2 }, new[] { 0.22, 0.22, 0.22, 0.22, 0.22, 0.7 }, 0.5), 0.35f);
      Set(Sfx.Tick, Melody(new[] { 15 }, new[] { 0.08 }, 0.5), 0.25f);
      Set(Sfx.Select, Melody(new[] { 22 }, new[] { 0.04 }, 0.25), 0.2f);
      Set(Sfx.Pause, Melody(new[] { 15, 10 }, new[] { 0.06, 0.1 }, 0.5), 0.25f);
      // フォークがふかす: 低くごろごろ震える音
      w = New(1.0);
      float glp = 0;
      double gph = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        glp += (Noise() - glp) * 0.08f;
        gph += (75 - 15 * t) / SR;
        double saw = (gph - Math.Floor(gph)) * 2 - 1;
        double trem = 0.55 + 0.45 * Math.Sin(t * PI2 * 24);
        double env = Math.Min(1, t / 0.15) * Math.Min(1, (1.0 - t) / 0.35);
        w[i] = (float)((saw * 0.5 + glp * 3.5) * trem * env);
      }
      Set(Sfx.Growl, w, 0.45f);
      // 爆弾を投げる: ヒューと下がっていく口笛のような音
      w = New(0.8);
      double bph2 = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        bph2 += (1500 - 900 * t) / SR;
        w[i] = (float)(Math.Sin(bph2 * PI2) * Math.Min(1, t / 0.05) * Math.Min(1, (0.8 - t) / 0.2) * 0.5);
      }
      Set(Sfx.Bomb, w, 0.16f);
      // 小さな爆発: ぽん、と軽くこもった音
      w = New(0.18);
      float flp = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        flp += (Noise() - flp) * 0.15f;
        w[i] = (float)((flp * 2.5 + Math.Sin(t * PI2 * (160 - 300 * t)) * 0.6) * Math.Exp(-t * 30));
      }
      Set(Sfx.Flak, w, 0.22f);

      // ---- FOUNDRY ----
      // 戦車の青い弾: トゥン (少し上から落ちて止まる、丸い音程のある音。短い余韻)
      w = New(0.16);
      double sph = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        sph += 560 * (1 + 0.7 * Math.Exp(-t * 70)) / SR;
        double v = Math.Sin(sph * PI2) + 0.25 * Math.Sin(sph * 2 * PI2) + 0.12 * Square(sph);
        w[i] = (float)(v * Math.Min(1, t / 0.003) * Math.Exp(-t * 24));
      }
      Set(Sfx.G2Shot, w, 0.12f);
      // 機械が壊れる: 爆発に、金属が打ち合うガシャンを重ねる
      w = Explosion(0.75, 0.07f, 55);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        w[i] += (float)((Math.Sin(t * PI2 * 310) + Math.Sin(t * PI2 * 487) * 0.8 + Math.Sin(t * PI2 * 733) * 0.6) * Math.Exp(-t * 14) * 0.35);
      }
      Set(Sfx.G2Boom, w, 0.32f);
      // 核が飛び出す: 震えながら上ずる電子音
      w = New(0.35);
      double cph = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR, k = t / 0.35;
        cph += (600 + 1400 * k + 150 * Math.Sin(t * PI2 * 30)) / SR;
        w[i] = (float)((Square(cph) * 0.4 + Math.Sin(cph * PI2) * 0.6) * Math.Min(1, t / 0.01) * (1 - k));
      }
      Set(Sfx.CoreFree, w, 0.1f);
      // 核を撃ち落とした: ガラスを打ったようなチン
      w = New(0.4);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        w[i] = (float)((Math.Sin(t * PI2 * 2093) + Math.Sin(t * PI2 * 3136) * 0.6) * Math.Exp(-t * 11) * 0.6 + Noise() * Math.Exp(-t * 90) * 0.3);
      }
      Set(Sfx.CoreKill, w, 0.2f);
      // 核が乗り移った: ぶるぶる震えながら下がる、こもったうなり
      w = New(0.6);
      double pph = 0;
      float plp = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR, k = t / 0.6;
        pph += (900 - 600 * k) / SR;
        double saw = (pph - Math.Floor(pph)) * 2 - 1;
        plp += (float)(saw * (0.6 + 0.4 * Math.Sin(t * PI2 * 40)) - plp) * 0.25f;
        w[i] = (float)(plp * Math.Min(1, t / 0.02) * Math.Pow(1 - k, 1.3));
      }
      Set(Sfx.Possess, w, 0.22f);
      // ウイルス: 音の高さがでたらめに跳ぶ、デジタルのジジジ
      w = New(0.8);
      double vph = 0, vf = 800;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        if (i % (SR * 3 / 100) == 0)
          vf = 400 + rng.NextDouble() * 2000;
        vph += vf / SR;
        double v = Square(vph) * 0.5 + ((i / 40) % 2 == 1 ? 0.25 : -0.25) * (rng.NextDouble() > 0.7 ? 1 : 0);
        w[i] = (float)(v * Math.Min(1, t / 0.01) * Math.Pow(1 - t / 0.8, 1.5));
      }
      Set(Sfx.Virus, w, 0.14f);
      // 弾をはじいた: キン
      w = New(0.14);
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        w[i] = (float)((Math.Sin(t * PI2 * (2600 - 1200 * t)) + Math.Sin(t * PI2 * 3900) * 0.5) * Math.Exp(-t * 30) * 0.6);
      }
      Set(Sfx.Ping, w, 0.1f);
      // 防犯カメラに見つかった: ピピッ
      w = New(0.32);
      for (int b = 0; b < 2; b++)
      {
        int st = (int)(SR * b * 0.14);
        for (int i = 0; st + i < w.Length && i < SR * 0.09; i++)
        {
          double t = (double)i / SR;
          w[st + i] = (float)((Square(t * 1320) * 0.4 + Math.Sin(t * PI2 * 1320) * 0.6) * Math.Min(1, t / 0.003) * Math.Min(1, (0.09 - t) / 0.01));
        }
      }
      Set(Sfx.Alarm, w, 0.13f);
      // 蒸気: シュー (高い雑音が吹き出して弱まる)
      w = New(0.5);
      float slp2 = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        float n = Noise();
        slp2 += (n - slp2) * 0.5f;
        w[i] = (float)((n - slp2) * Math.Min(1, t / 0.02) * Math.Pow(1 - t / 0.5, 1.5));
      }
      Set(Sfx.Steam, w, 0.12f);
      // 戦車の足が着く: 小さなカシャ (サーボのカチッと、低いトン)
      w = New(0.08);
      float flp2 = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        flp2 += (Noise() - flp2) * 0.3f;
        w[i] = (float)(flp2 * 1.2 * Math.Exp(-t * 70) + Math.Sin(t * PI2 * 170) * Math.Exp(-t * 45) * 0.6 + Math.Sin(t * PI2 * 3100) * Math.Exp(-t * 160) * 0.3);
      }
      Set(Sfx.Step, w, 0.07f);
      // フックやプレスが落ちる: ズシンと、鉄の響き
      w = New(0.7);
      float mlp = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR;
        mlp += (Noise() - mlp) * 0.06f;
        double body = Math.Tanh(Math.Sin(t * PI2 * (80 - 50 * t)) * 2.2) * Math.Exp(-t * 7);
        double ring = (Math.Sin(t * PI2 * 220) + Math.Sin(t * PI2 * 347) * 0.7) * Math.Exp(-t * 6) * 0.3;
        w[i] = (float)(body * 0.8 + mlp * 4 * Math.Exp(-t * 9) + ring);
      }
      Set(Sfx.Slam, w, 0.42f);
      // 出発: 低めの、少し暗い工場らしい節
      Set(Sfx.G2Start, Melody(new[] { -12, -5, 0, 3, 2, -2, 0 }, new[] { 0.12, 0.12, 0.12, 0.24, 0.12, 0.12, 0.5 }, 0.25), 0.3f);
      // 背景: 遠くのくい打ち。重い打撃 (歪ませた低い音) と鉄の響きをこもらせて、こだまを 2 つ
      w = New(1.1);
      float klp = 0, klp2 = 0;
      for (int i = 0; i < (int)(SR * 0.6); i++)
      {
        double t = (double)i / SR;
        double thud = Math.Tanh(Math.Sin(t * PI2 * (95 - 45 * Math.Min(1, t * 8))) * 2.5) * Math.Exp(-t * 9);
        double clang = (Math.Sin(t * PI2 * 183) + Math.Sin(t * PI2 * 412) * 0.8 + Math.Sin(t * PI2 * 655) * 0.6 + Math.Sin(t * PI2 * 1021) * 0.3) * Math.Exp(-t * 7);
        double hit = Noise() * Math.Exp(-t * 60);
        float v = (float)(thud * 0.9 + clang * 0.35 + hit * 0.5);
        klp += (v - klp) * 0.35f; // 遠いのでこもらせる
        klp2 += (klp - klp2) * 0.5f;
        w[i] = klp2 * (float)Math.Min(1, t / 0.002);
      }
      int e1 = (int)(SR * 0.19), e2 = (int)(SR * 0.41);
      for (int i = w.Length - 1; i >= 0; i--)
      {
        if (i >= e1)
          w[i] += w[i - e1] * 0.32f;
        if (i >= e2)
          w[i] += w[i - e2] * 0.15f;
      }
      Set(Sfx.PileDrive, w, 0.3f);
      // 背景: 遠くの大きな機械。ガシャンと噛み合う音のあとに、油圧が抜けるシュー
      w = New(1.0);
      float hlp = 0, hlp2 = 0;
      for (int i = 0; i < w.Length; i++)
      {
        double t = (double)i / SR, v = 0;
        for (int k = 0; k < 2; k++) // ガシャ、ン (2 つに分かれて当たる)
        {
          double u = t - k * 0.07;
          if (u >= 0)
            v += ((Math.Sin(u * PI2 * 240) + Math.Sin(u * PI2 * 377) * 0.8 + Math.Sin(u * PI2 * 590) * 0.5) * 0.4 + Noise() * 0.5) * Math.Exp(-u * 22) * (k != 0 ? 1.0 : 0.6);
        }
        if (t > 0.18) // シュー
        {
          double u = t - 0.18;
          float n = Noise();
          hlp += (n - hlp) * 0.4f;
          v += (n - hlp) * 0.35 * Math.Min(1, u / 0.05) * Math.Pow(Math.Max(0, 1 - u / 0.8), 1.5);
        }
        hlp2 += ((float)v - hlp2) * 0.45f; // 遠いのでこもらせる
        w[i] = hlp2;
      }
      Set(Sfx.MachClank, w, 0.2f);
    }
  }
}
