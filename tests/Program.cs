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
}
