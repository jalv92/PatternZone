using System;

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
            Console.WriteLine(T.Failures == 0 ? "ALL PASS" : T.Failures + " FAILURES");
            return T.Failures == 0 ? 0 : 1;
        }
    }
}
