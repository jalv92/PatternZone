using System;
using System.Collections.Generic;
using PatternZoneCore;

namespace PatternZone.Tests
{
    public static class T
    {
        public static int Failures;
        public static void Check(bool ok, string name)
        {
            if (ok) { Console.WriteLine("  PASS " + name); return; }
            Failures++;
            Console.WriteLine("  FAIL " + name);
        }
        public static void CheckClose(double a, double b, string name, double eps = 1e-9)
        {
            Check(Math.Abs(a - b) <= eps, name + " (" + a + " vs " + b + ")");
        }
    }

    public static class Program
    {
        public static int Main()
        {
            // Each task appends its suite here.
            T.Check(true, "smoke");
            CoreTests.Run();
            DoubleTests.Run();
            TripleHsTests.Run();
            ZoneTests.Run();
            FlagTests.Run();
            EngineTests.Run();
            Console.WriteLine(T.Failures == 0 ? "ALL PASS" : T.Failures + " FAILURES");
            return T.Failures == 0 ? 0 : 1;
        }
    }

    public static class CoreTests
    {
        static PzBar B(double o, double h, double l, double c)
        {
            return new PzBar { Time = new DateTime(2026, 8, 12, 9, 30, 0), Open = o, High = h, Low = l, Close = c };
        }

        public static void Run()
        {
            Console.WriteLine("CoreTests");

            // Wilder ATR: bar0 seed = h-l; bar1 = mean of tr0,tr1.
            var atr = new WilderAtr(14);
            atr.Update(B(100, 102, 100, 101));            // tr0 = 2
            T.CheckClose(atr.Value, 2.0, "atr seed");
            atr.Update(B(101, 105, 101, 104));            // tr1 = max(4, |105-101|, |101-101|) = 4
            T.CheckClose(atr.Value, 3.0, "atr running mean");

            // SwingDetector strength 2: V shape confirms a swing low 2 bars later.
            var sd = new SwingDetector(2);
            double[] closes = { 105, 104, 100, 104, 105 };  // low pivot at index 2
            List<PzSwing> got = null;
            for (int i = 0; i < closes.Length; i++)
            {
                var r = sd.Update(B(closes[i], closes[i] + 0.5, closes[i] - 0.5, closes[i]), i);
                if (r.Count > 0) got = r;
            }
            T.Check(got != null && got.Count == 1 && !got[0].IsHigh, "swing low confirmed");
            T.CheckClose(got[0].Price, 99.5, "swing low price");   // low of bar 2
            T.Check(got[0].BarIndex == 2, "swing low bar index");

            // Non-unique extreme (two equal highs in window) confirms nothing.
            var sd2 = new SwingDetector(1);
            sd2.Update(B(100, 103, 99, 100), 0);
            sd2.Update(B(100, 103, 99, 100), 1);
            var r2 = sd2.Update(B(100, 101, 99, 100), 2);
            T.Check(r2.Count == 0, "tied extreme rejected");
        }
    }

    public static class DoubleTests
    {
        static PzSwing S(int bar, double px, bool hi)
        {
            return new PzSwing { BarIndex = bar, Time = new DateTime(2026, 8, 12).AddMinutes(bar), Price = px, IsHigh = hi };
        }

        public static void Run()
        {
            Console.WriteLine("DoubleTests");
            var cfg = new PzConfig();          // TopToleranceAtr 0.30
            double atr = 2.0;                  // tolerance = 0.6

            var sw = new List<PzSwing> { S(0, 100, false), S(10, 110, true), S(15, 105, false), S(20, 110.3, true) };
            var c = PatternScanner.TryDouble(sw, cfg, atr);
            T.Check(c != null && c.Kind == PatternKind.DoubleTop && c.IsShort, "double top found");
            T.CheckClose(c.ExtremePrice, 110.3, "DT extreme = higher top");
            T.CheckClose(c.NecklineAt(25), 105.0, "DT neckline horizontal at valley");
            T.Check(c.ZoneExtremes.Length == 2, "DT zone extremes = both tops");

            // Tops too far apart (diff 0.7 > 0.6) -> null.
            var sw2 = new List<PzSwing> { S(0, 100, false), S(10, 110, true), S(15, 105, false), S(20, 110.7, true) };
            T.Check(PatternScanner.TryDouble(sw2, cfg, atr) == null, "tolerance rejects");

            // Mirror: double bottom -> long.
            var sw3 = new List<PzSwing> { S(0, 110, true), S(10, 100, false), S(15, 104, true), S(20, 100.2, false) };
            var c3 = PatternScanner.TryDouble(sw3, cfg, atr);
            T.Check(c3 != null && c3.Kind == PatternKind.DoubleBottom && !c3.IsShort, "double bottom found");
            T.CheckClose(c3.ExtremePrice, 100.0, "DB extreme = lower bottom");
        }
    }

    public static class TripleHsTests
    {
        static PzSwing S(int bar, double px, bool hi)
        {
            return new PzSwing { BarIndex = bar, Time = new DateTime(2026, 8, 12).AddMinutes(bar), Price = px, IsHigh = hi };
        }

        public static void Run()
        {
            Console.WriteLine("TripleHsTests");
            var cfg = new PzConfig(); double atr = 2.0;   // tol 0.6, prominence 0.6

            // Triple top: 3 tops within 0.6, neckline = LOWER of the two valleys.
            var tt = new List<PzSwing> { S(0,100,false), S(5,110,true), S(8,106,false), S(12,110.4,true), S(15,104.5,false), S(20,110.2,true) };
            var c = PatternScanner.TryTriple(tt, cfg, atr);
            T.Check(c != null && c.Kind == PatternKind.TripleTop && c.IsShort, "triple top found");
            T.CheckClose(c.NecklineAt(30), 104.5, "TT neckline = worst valley");
            T.CheckClose(c.ExtremePrice, 110.4, "TT extreme = highest top");
            T.Check(c.ZoneExtremes.Length == 3, "TT zone extremes = 3 tops");

            // H&S: head above both shoulders by >= 0.6; shoulders within 0.6; sloped neckline.
            var hs = new List<PzSwing> { S(0,100,false), S(5,108,true), S(8,105,false), S(12,110,true), S(15,106,false), S(20,108.3,true) };
            var h = PatternScanner.TryHeadShoulders(hs, cfg, atr);
            T.Check(h != null && h.Kind == PatternKind.HeadShoulders && h.IsShort, "H&S found");
            T.CheckClose(h.ExtremePrice, 110.0, "HS extreme = head");
            // Neckline through (8,105) and (15,106): at bar 22 -> 105 + (22-8)*(1/7) = 107
            T.CheckClose(h.NecklineAt(22), 107.0, "HS sloped neckline extension");
            T.Check(h.ZoneExtremes.Length == 1 && h.ZoneExtremes[0] == 110.0, "HS zone extreme = head only");

            // Same 5 swings but head prominence too small (109.0 head vs 108/108.3 shoulders, prominence 0.6) -> null.
            var hs2 = new List<PzSwing> { S(0,100,false), S(5,108,true), S(8,105,false), S(12,108.5,true), S(15,106,false), S(20,108.3,true) };
            T.Check(PatternScanner.TryHeadShoulders(hs2, cfg, atr) == null, "prominence rejects");

            // Inverse H&S mirrors to long.
            var ihs = new List<PzSwing> { S(0,112,true), S(5,104,false), S(8,107,true), S(12,102,false), S(15,106,true), S(20,103.8,false) };
            var hi = PatternScanner.TryHeadShoulders(ihs, cfg, atr);
            T.Check(hi != null && hi.Kind == PatternKind.InverseHeadShoulders && !hi.IsShort, "inverse H&S found");
        }
    }

    public static class EngineTests
    {
        static int _bar;

        // Wander bars are H = c+1, L = c-1, so with SwingStrength 2 a swing
        // confirms exactly where the close series has a STRICT local extreme of
        // radius 2 (the detector's strict-unique window test reduces to that
        // when every bar has the same shape), a swing high prices at close+1 and
        // a swing low at close-1. Every sequence below is scripted against that.
        //
        // M into 110 with a deep valley: top1 high 110.0 (bar 6, confirms 8),
        // valley low 100.0 (bar 10, confirms 12), top2 high 110.2 (bar 15,
        // confirms 17 -> arms the double top), then closes fall through the
        // neckline: 99.4 <= 100.0 - NecklineBreakTicks*TickSize (0.5) on bar 20.
        // Height 10.2 clears MinPatternHeightAtr*atr (3.0) and both tops sit
        // inside the PDH-110 band (+/- ZoneHalfWidthAtr*atr = 1.0).
        static readonly double[] MAt110 = {
            100, 101, 102, 103, 105, 107, 109,
            108, 105, 103, 101,
            102, 104, 106, 108, 109.2,
            108, 106, 103, 100.5, 99.4
        };

        // Same M, shallow valley: top1 110.0 (bar 5), valley 107.5 (bar 8),
        // top2 110.2 (bar 12), break on bar 16 (106.8 <= 107.5 - 0.5).
        // Height 2.7 < 3.0 -> rejected on height BEFORE the zone test, even
        // though both tops sit on PDH 110.
        static readonly double[] ShortMAt110 = {
            105, 106, 107, 108, 108.7, 109, 108.8, 108.6, 108.5,
            108.55, 108.7, 108.9, 109.2, 109.0, 108.6, 107.9, 106.8
        };

        static double[] Shift(double[] closes, double delta)
        {
            var r = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
                r[i] = closes[i] + delta;
            return r;
        }

        static List<PzAction> Feed(PzEngine e, double o, double h, double l, double c, bool canTrade = true, double atrOverride = 2.0)
        {
            var b = new PzBar { Time = new DateTime(2026, 8, 12, 9, 30, 0).AddMinutes(_bar), Open = o, High = h, Low = l, Close = c };
            return e.OnBarClosed(b, atrOverride, canTrade);
        }

        static List<PzAction> Wander(PzEngine e, params double[] closes)
        {
            return Wander(e, true, closes);
        }

        static List<PzAction> Wander(PzEngine e, bool canTrade, double[] closes)
        {
            var all = new List<PzAction>();
            foreach (double c in closes) { all.AddRange(Feed(e, c, c + 1, c - 1, c, canTrade)); _bar++; }
            return all;
        }

        // One hand-shaped bar: flag bars need a range tighter than Wander's
        // fixed 2.0, which is exactly FlagRangeMaxAtr * atr (would re-anchor).
        static List<PzAction> One(PzEngine e, double o, double h, double l, double c, double atrOverride = 2.0)
        {
            var r = Feed(e, o, h, l, c, true, atrOverride);
            _bar++;
            return r;
        }

        static PzAction Find(List<PzAction> acts, PzActionType type)
        {
            return acts.Find(x => x.Type == type);
        }

        static PzAction Rejected(List<PzAction> acts, string reason)
        {
            return acts.Find(x => x.Type == PzActionType.DrawRejected && x.RejectReason == reason);
        }

        static PzEngine Fresh(PzConfig cfg)
        {
            _bar = 0;
            var e = new PzEngine(cfg);
            e.OnSessionOpen(new SessionLevels { PriorDayHigh = 110.0 });
            return e;
        }

        public static void Run()
        {
            Console.WriteLine("EngineTests");
            var cfg = new PzConfig { SwingStrength = 2 };
            var e = Fresh(cfg);
            T.CheckClose(e.Levels.PriorDayHigh, 110.0, "levels pass-through");

            // --- Entry: one M at PDH 110 -> exactly one short, drawn once.
            var acts = Wander(e, MAt110);
            int enters = 0, draws = 0;
            foreach (var a in acts) { if (a.Type == PzActionType.EnterShort) enters++; if (a.Type == PzActionType.DrawPattern) draws++; }
            T.Check(enters == 1, "one EnterShort");
            T.Check(draws == 1, "one DrawPattern");
            T.Check(Find(acts, PzActionType.DrawRejected) == null, "clean entry rejects nothing");

            PzAction ent = Find(acts, PzActionType.EnterShort);
            T.Check(ent.Pattern != null && ent.Pattern.IsShort, "action carries pattern");
            T.Check(ent.StopPrice > ent.Pattern.ExtremePrice, "stop beyond extreme");
            T.Check(ent.TargetPrice < ent.Pattern.NecklineAt(_bar), "target below neckline");
            T.CheckClose(ent.StopPrice, 111.2, "stop = extreme + StopBufferAtr*atr");
            T.CheckClose(ent.TargetPrice, 89.8, "target = neckline - height*TargetMultiple");

            // --- Flag add-on, anchored on the fill (99.4 @ bar 20).
            e.OnEntryFilled(99.4);
            var pole = Wander(e, 97, 95, 94);                       // pole low 93: 6.4 >= PoleMinAtr*atr (4.0)
            T.Check(Find(pole, PzActionType.AddShort) == null, "no add while the pole runs");
            One(e, 94, 94.8, 93.6, 94.4);                           // flag bar 1
            One(e, 94.4, 94.9, 93.8, 94.2);                         // flag bar 2
            var flag3 = One(e, 94.2, 94.7, 93.7, 94.0);             // flag bar 3 (FlagMinBars)
            T.Check(Find(flag3, PzActionType.AddShort) == null, "no add while the flag builds");
            var brk = One(e, 94.0, 94.1, 92.8, 93.2);               // 93.2 <= flagLow 93.6 - tick
            PzAction add = Find(brk, PzActionType.AddShort);
            T.Check(add != null, "AddShort emitted");
            T.Check(add.Flag != null && add.StopPrice > add.Flag.FlagHigh, "aggregate stop beyond flag");
            T.CheckClose(add.StopPrice, 95.9, "aggregate stop = flag high + StopBufferAtr*atr");
            T.Check(Find(brk, PzActionType.DrawFlag) != null, "DrawFlag rides with the add");
            e.OnAddFilled(93.2);

            // --- MaxAdds = 1: a second flag adds nothing.
            var more = Wander(e, 91.5, 90, 89, 89.2, 89.4, 89.1, 88.0);
            T.Check(Find(more, PzActionType.AddShort) == null, "MaxAdds respected");

            // --- Busy: the same M ten points lower would enter if we were flat
            // (height 10.2, tops on the round-100 level) -> rejected as busy.
            var busy = Wander(e, Shift(MAt110, -10));
            T.Check(Find(busy, PzActionType.EnterShort) == null, "no second entry while in position");
            T.Check(Rejected(busy, "busy") != null, "busy rejection");
            e.OnPositionClosed();

            // --- Zone: same M four points lower -> tops at 106 touch no level
            // (PDH 110 and round-100 are both > 1.0 away). Height still passes,
            // so the reason must be the zone.
            var eZone = Fresh(new PzConfig { SwingStrength = 2 });
            var zoneActs = Wander(eZone, Shift(MAt110, -4));
            T.Check(Find(zoneActs, PzActionType.EnterShort) == null, "out-of-zone M does not enter");
            T.Check(Rejected(zoneActs, "zone") != null, "zone rejection");

            // --- Height: M on PDH 110 but only 2.7 tall.
            var eHeight = Fresh(new PzConfig { SwingStrength = 2 });
            var heightActs = Wander(eHeight, ShortMAt110);
            T.Check(Find(heightActs, PzActionType.EnterShort) == null, "under-height M does not enter");
            T.Check(Rejected(heightActs, "height") != null, "height rejection");

            // --- Session cap: MaxTradesPerSession 1, flat again, second M rejects.
            var eCap = Fresh(new PzConfig { SwingStrength = 2, MaxTradesPerSession = 1 });
            var first = Wander(eCap, MAt110);
            T.Check(Find(first, PzActionType.EnterShort) != null, "first entry of the capped session");
            eCap.OnEntryFilled(99.4);
            eCap.OnPositionClosed();
            var second = Wander(eCap, MAt110);
            T.Check(Find(second, PzActionType.EnterShort) == null, "capped session does not enter again");
            T.Check(Rejected(second, "session_cap") != null, "session_cap rejection");

            // --- A rejected add releases the awaiting-add state and fails safe.
            // TargetMultiple 2.0 puts the target at 79.6 so the second flag has
            // room to spare on the distance guard: if the detector were still
            // live, that flag WOULD add, which is what makes this a real check.
            var eAdd = Fresh(new PzConfig { SwingStrength = 2, TargetMultiple = 2.0 });
            Wander(eAdd, MAt110);
            eAdd.OnEntryFilled(99.4);
            Wander(eAdd, 97, 95, 94);
            One(eAdd, 94, 94.8, 93.6, 94.4);
            One(eAdd, 94.4, 94.9, 93.8, 94.2);
            One(eAdd, 94.2, 94.7, 93.7, 94.0);
            var addBrk = One(eAdd, 94.0, 94.1, 92.8, 93.2);
            T.Check(Find(addBrk, PzActionType.AddShort) != null, "add emitted before the failure");
            eAdd.OnAddFailed();

            // (a) the same proven pole+flag shape six points lower, measured
            // from where a still-armed detector would have re-anchored (93.2).
            var afterFail = Wander(eAdd, 91, 89, 88);
            One(eAdd, 88, 88.8, 87.6, 88.4);
            One(eAdd, 88.4, 88.9, 87.8, 88.2);
            One(eAdd, 88.2, 88.7, 87.7, 88.0);
            afterFail.AddRange(One(eAdd, 88.0, 88.1, 86.8, 87.2));
            T.Check(Find(afterFail, PzActionType.AddShort) == null, "no add after OnAddFailed");

            // (b) still in position, so a new confirmation is still busy.
            var busyAfterFail = Wander(eAdd, Shift(MAt110, -10));
            T.Check(Rejected(busyAfterFail, "busy") != null, "busy still rejects after OnAddFailed");
            T.Check(Find(busyAfterFail, PzActionType.EnterShort) == null, "no entry after OnAddFailed");

            // (c) flat + new session recovers normal trading (two extra closes
            // for the triple-neckline case, as in the session-reset check).
            eAdd.OnPositionClosed();
            eAdd.OnSessionOpen(new SessionLevels { PriorDayHigh = 110.0 });
            var recovered = Wander(eAdd, MAt110);
            recovered.AddRange(Wander(eAdd, 98, 97));
            T.Check(Find(recovered, PzActionType.EnterShort) != null, "trading recovers after OnAddFailed");

            // --- Unready ATR (warmup): every gate is ATR-scaled, so both action
            // paths sit out the bar. (a) the M arms normally, then its break bar
            // arrives with atr 0 -> nothing at all. The break still resolved the
            // candidate, so a later healthy bar below the neckline is silent too.
            var eAtr = Fresh(new PzConfig { SwingStrength = 2 });
            Wander(eAtr, new List<double>(MAt110).GetRange(0, 20).ToArray());   // arms at bar 17, no break yet
            var atrZero = One(eAtr, 99.4, 100.4, 98.4, 99.4, 0.0);
            T.Check(atrZero.Count == 0, "unready ATR emits nothing on the break bar");
            var afterAtr = One(eAtr, 99.0, 100.0, 98.0, 99.0);
            T.Check(afterAtr.Count == 0, "the break consumed the candidate even at atr 0");

            // (b) same guard on the add path: a formed pole+flag whose trigger
            // bar carries atr 0 adds nothing, and the detector is only skipped —
            // the next healthy bar with the same shape still fires the add.
            var eAtrAdd = Fresh(new PzConfig { SwingStrength = 2 });
            Wander(eAtrAdd, MAt110);
            eAtrAdd.OnEntryFilled(99.4);
            Wander(eAtrAdd, 97, 95, 94);
            One(eAtrAdd, 94, 94.8, 93.6, 94.4);
            One(eAtrAdd, 94.4, 94.9, 93.8, 94.2);
            One(eAtrAdd, 94.2, 94.7, 93.7, 94.0);
            var addAtrZero = One(eAtrAdd, 94.0, 94.1, 92.8, 93.2, 0.0);
            T.Check(Find(addAtrZero, PzActionType.AddShort) == null, "unready ATR emits no add");
            var addAfter = One(eAtrAdd, 94.0, 94.1, 92.8, 93.2);
            T.Check(Find(addAfter, PzActionType.AddShort) != null, "add fires once ATR is ready again");

            // --- canTrade false: the shell already knows why, so nothing at all
            // is emitted (not even a rejection).
            var eLocked = Fresh(new PzConfig { SwingStrength = 2 });
            var locked = Wander(eLocked, false, MAt110);
            T.Check(locked.Count == 0, "canTrade=false emits nothing");

            // --- OnSessionOpen resets the trade count but keeps the structure:
            // the third M arms a TRIPLE top off swings the capped session left
            // behind (rejected patterns consume nothing), so its neckline is the
            // worst of the two valleys (98.4) and the break needs two more bars.
            eCap.OnSessionOpen(new SessionLevels { PriorDayHigh = 110.0 });
            var third = Wander(eCap, MAt110);
            third.AddRange(Wander(eCap, 98, 97));
            T.Check(Find(third, PzActionType.EnterShort) != null, "new session re-opens the trade budget");
            T.Check(Rejected(third, "session_cap") == null, "no session_cap after the reset");
        }
    }

    public static class ZoneTests
    {
        public static void Run()
        {
            Console.WriteLine("ZoneTests");
            var cfg = new PzConfig();                       // half-width 0.5 * atr
            var z = new ZoneEngine(cfg);
            double atr = 2.0;                               // band = level +/- 1.0
            z.SetLevels(new SessionLevels { PriorDayHigh = 110.1, PriorDayLow = 90.0, PriorClose = 100.0 });

            var dt = new PatternCandidate
            {
                Kind = PatternKind.DoubleTop, IsShort = true,
                ZoneExtremes = new[] { 110.0, 110.3 }, ExtremePrice = 110.3
            };
            double lvl;
            T.Check(z.Permits(dt, atr, out lvl), "DT at PDH permitted");
            T.CheckClose(lvl, 110.1, "zone level = PDH");

            // Both tops must share ONE band: 110 fits PDH band, 108.5 fits nothing.
            var dt2 = new PatternCandidate { Kind = PatternKind.DoubleTop, IsShort = true, ZoneExtremes = new[] { 110.0, 108.5 }, ExtremePrice = 110.0 };
            T.Check(!z.Permits(dt2, atr, out lvl), "split tops rejected");

            // Round-100: extremes near 30000 permitted with no session level nearby.
            var z2 = new ZoneEngine(cfg);
            z2.SetLevels(new SessionLevels());              // all NaN
            var dt3 = new PatternCandidate { Kind = PatternKind.DoubleTop, IsShort = true, ZoneExtremes = new[] { 29999.5, 30000.75 }, ExtremePrice = 30000.75 };
            T.Check(z2.Permits(dt3, atr, out lvl), "round-100 zone works");
            T.CheckClose(lvl, 30000.0, "nearest round level");

            // Round-50 off by default.
            var dt4 = new PatternCandidate { Kind = PatternKind.DoubleTop, IsShort = true, ZoneExtremes = new[] { 29949.8, 29950.2 }, ExtremePrice = 29950.2 };
            T.Check(!z2.Permits(dt4, atr, out lvl), "round-50 off by default");
            cfg.UseRound50 = true;
            T.Check(z2.Permits(dt4, atr, out lvl), "round-50 on when toggled");
            cfg.UseRound50 = false;

            // Triple: 2 of 3 suffice. H&S: head only.
            var tt = new PatternCandidate { Kind = PatternKind.TripleTop, IsShort = true, ZoneExtremes = new[] { 110.0, 110.4, 108.0 }, ExtremePrice = 110.4 };
            T.Check(z.Permits(tt, atr, out lvl), "triple 2-of-3 permitted");
            var hs = new PatternCandidate { Kind = PatternKind.HeadShoulders, IsShort = true, ZoneExtremes = new[] { 110.5 }, ExtremePrice = 110.5 };
            T.Check(z.Permits(hs, atr, out lvl), "HS head permitted");
        }
    }

    public static class FlagTests
    {
        static PzBar B(double o, double h, double l, double c)
        {
            return new PzBar { Time = new DateTime(2026, 8, 12, 10, 0, 0), Open = o, High = h, Low = l, Close = c };
        }

        public static void Run()
        {
            Console.WriteLine("FlagTests");
            var cfg = new PzConfig();          // PoleMin 2.0*atr, flag 3-10 bars, range <= 1.0*atr
            var fd = new FlagDetector(cfg);
            double atr = 2.0;                  // pole >= 4.0, flag range <= 2.0
            fd.Arm(1, 100.0, 0);               // long from 100

            FlagInfo got = null; int bar = 1;
            // Pole: 3 bars up to 105 (move 5 >= 4).
            foreach (var b in new[] { B(100,102,100,102), B(102,104,102,104), B(104,105,103.5,104.8) })
                { got = fd.Update(b, bar++, atr); T.Check(got == null, "no trigger during pole " + bar); }
            // Flag: 3 tight bars, envelope [103.8, 104.9] (range 1.1 <= 2).
            foreach (var b in new[] { B(104.8,104.9,104.0,104.2), B(104.2,104.5,103.8,104.0), B(104.0,104.6,103.9,104.5) })
                { got = fd.Update(b, bar++, atr); T.Check(got == null, "no trigger during flag " + bar); }
            // Breakout close 105.4 >= FlagHigh 104.9 + tick 0.25.
            got = fd.Update(B(104.5, 105.5, 104.4, 105.4), bar++, atr);
            T.Check(got != null, "flag breakout triggers");
            T.CheckClose(got.FlagHigh, 104.9, "flag high");
            T.CheckClose(got.FlagLow, 103.8, "flag low");
            T.CheckClose(got.PoleEndPrice, 105.0, "pole extreme");

            // Wide 'flag' (envelope > 2.0) never triggers: detector re-anchors.
            var fd2 = new FlagDetector(cfg);
            fd2.Arm(1, 100.0, 0); bar = 1;
            foreach (var b in new[] { B(100,102,100,102), B(102,104,102,104), B(104,105,103.5,104.8),
                                      B(104.8,105,101,101.5), B(101.5,104.9,101,104.6), B(104.6,104.9,104,104.2) })
                T.Check(fd2.Update(b, bar++, atr) == null, "wide flag never triggers " + bar);

            // Orphan guard: an all-extension run past PoleMaxBars (8) with no
            // pullback bar must not get the detector stuck once the first
            // pullback finally arrives.
            var fd3 = new FlagDetector(cfg);
            fd3.Arm(1, 100.0, 0); bar = 1;
            for (int i = 1; i <= 10; i++)
            {
                double h = 100 + i;
                T.Check(fd3.Update(B(h - 1, h, h - 2, h - 0.5), bar++, atr) == null, "no trigger during overrun climb " + bar);
            }
            // extremeBar(10) - anchorBar(0) = 10 > PoleMaxBars(8) -> re-anchor
            // here instead of orphaning (PoleExists() could never pass again).
            got = fd3.Update(B(108, 109, 107.5, 108.0), bar++, atr);
            T.Check(got == null, "overrun pullback re-anchors, no trigger");

            // Fresh pole (108 -> 112, move 4 >= 4) + 3-bar flag + breakout
            // from the NEW anchor (bar 11) proves recovery, not orphaning.
            got = fd3.Update(B(108, 110, 108, 109.5), bar++, atr);
            T.Check(got == null, "new pole building 1");
            got = fd3.Update(B(109.5, 112, 109, 111.5), bar++, atr);
            T.Check(got == null, "new pole building 2");
            got = fd3.Update(B(111.5, 111.9, 111.0, 111.5), bar++, atr);
            T.Check(got == null, "new flag bar 1");
            got = fd3.Update(B(111.5, 111.8, 110.8, 111.2), bar++, atr);
            T.Check(got == null, "new flag bar 2");
            got = fd3.Update(B(111.2, 111.7, 110.9, 111.3), bar++, atr);
            T.Check(got == null, "new flag bar 3");
            got = fd3.Update(B(111.3, 112.5, 111.2, 112.3), bar++, atr);
            T.Check(got != null, "new pole breakout triggers after orphan-guard recovery");
            T.CheckClose(got.PoleStartPrice, 108.0, "new pole start price");
            T.CheckClose(got.PoleEndPrice, 112.0, "new pole end price");
            T.Check(got.PoleStartBar == 11, "new pole start bar");
            T.Check(got.PoleEndBar == 13, "new pole end bar");
            T.CheckClose(got.FlagHigh, 111.9, "new flag high");
            T.CheckClose(got.FlagLow, 110.8, "new flag low");
        }
    }
}
