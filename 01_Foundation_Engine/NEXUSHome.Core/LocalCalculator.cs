using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>Result of one /calc evaluation. <see cref="Message"/> is the line shown on the HUD.</summary>
    public sealed record LocalCalcResult(
        bool Ok,
        string Expression,
        string Message,
        double? Value,
        long? IntegerValue,
        string? Hex,
        string? Bin,
        string? Error);

    /// <summary>
    /// TASK-14 ADR-59 <c>/calc</c> (aliases <c>/c</c>, <c>/=</c>): a hand-written recursive-descent evaluator. No Regex, no
    /// dynamic IL, no expression trees (ADR-29: nothing that grows the LoaderHeap), 0 tokens, 0 network.
    /// Precedence, lowest first: <c>|</c>, <c>&amp;</c>, <c>&lt;&lt; &gt;&gt;</c>, <c>+ -</c>, <c>* / %</c>, unary <c>+ -</c>,
    /// <c>^</c> / <c>**</c> (right-associative, so <c>-2^2 = -4</c> and <c>2^3^2 = 512</c>).
    /// Integers stay exact in Int64 (decimal, <c>0x</c> hex, <c>0b</c> binary); an Int64 overflow in <c>+ - * ^</c> moves
    /// the value to double. An integral result inside Int64 is printed in Dec, Hex and Bin.
    /// Every failure (syntax, division by zero, NaN, infinity, nesting too deep) becomes <c>[CALC ERROR] ...</c>;
    /// <see cref="Evaluate"/> never throws.
    /// </summary>
    public static class LocalCalculator
    {
        public const int MaxExpressionChars = 256;
        private const int MaxDepth = 48; // nested parentheses / unary operators; keeps the recursion far from a StackOverflow

        public const string Usage =
            "Dùng: /calc <biểu thức> (bí danh /c, /=). Phép toán: + - * / % ^ ( ) << >> & | ; " +
            "hàm: sqrt abs round floor ceil min max pow log (ln) log2 log10 sin cos tan; hằng: pi e; số: 255, 0xFF, 0b1010, 1.5e3";

        public static LocalCalcResult Evaluate(string? expression)
        {
            string expr = (expression ?? string.Empty).Trim();
            if (expr.Length == 0) return Fail(expr, "Thiếu biểu thức. Ví dụ: /calc sqrt(144) + 2^8");
            if (expr.Length > MaxExpressionChars) return Fail(Shorten(expr), $"Biểu thức dài quá {MaxExpressionChars} ký tự.");

            try
            {
                var parser = new Parser(expr);
                Num value = parser.ParseAll();
                return Format(expr, value);
            }
            catch (CalcException ex)
            {
                return Fail(expr, ex.Message);
            }
            catch (Exception ex)
            {
                // Defensive: the parser only throws CalcException, but a /calc must never take the Cockpit down.
                return Fail(expr, $"Lỗi nội bộ ({ex.GetType().Name}).");
            }
        }

        private static LocalCalcResult Fail(string expr, string error) =>
            new(false, expr, $"[CALC ERROR] {error}", null, null, null, null, error);

        private static string Shorten(string text) => text.Length > 40 ? text.Substring(0, 40) + "…" : text;

        private static LocalCalcResult Format(string expr, Num value)
        {
            if (TryAsInt64(value, out long integer))
            {
                string dec = integer.ToString(CultureInfo.InvariantCulture);
                ulong magnitude = Magnitude(integer);
                string sign = integer < 0 ? "-" : string.Empty;
                string hex = $"{sign}0x{magnitude.ToString("X", CultureInfo.InvariantCulture)}";
                string bin = $"{sign}0b{ToBinary(magnitude)}";
                return new LocalCalcResult(true, expr, $"[CALC] {expr} = {dec} (Hex: {hex} | Bin: {bin})",
                    integer, integer, hex, bin, null);
            }

            double d = value.AsDouble;
            string text = d.ToString("G15", CultureInfo.InvariantCulture);
            return new LocalCalcResult(true, expr, $"[CALC] {expr} = {text}", d, null, null, null, null);
        }

        private static bool TryAsInt64(Num value, out long integer)
        {
            if (value.IsInt)
            {
                integer = value.I;
                return true;
            }
            double d = value.D;
            // [-2^63, 2^63): the double range that converts to Int64 without overflow.
            if (Math.Floor(d) == d && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18)
            {
                integer = (long)d;
                return true;
            }
            integer = 0;
            return false;
        }

        private static ulong Magnitude(long v) => v >= 0 ? (ulong)v : (ulong)(-(v + 1)) + 1UL;

        private static string ToBinary(ulong v)
        {
            if (v == 0) return "0";
            Span<char> buffer = stackalloc char[64];
            int pos = 64;
            while (v != 0)
            {
                buffer[--pos] = (v & 1UL) == 0 ? '0' : '1';
                v >>= 1;
            }
            return new string(buffer.Slice(pos));
        }

        private sealed class CalcException : Exception
        {
            public CalcException(string message) : base(message) { }
        }

        /// <summary>Exact Int64 or double.</summary>
        private readonly struct Num
        {
            private Num(bool isInt, long i, double d)
            {
                IsInt = isInt;
                I = i;
                D = d;
            }

            public bool IsInt { get; }
            public long I { get; }
            public double D { get; }
            public double AsDouble => IsInt ? I : D;

            public static Num Int(long v) => new(true, v, 0);

            public static Num Real(double v)
            {
                if (double.IsNaN(v)) throw new CalcException("Kết quả không xác định (NaN), ví dụ sqrt của số âm.");
                if (double.IsInfinity(v)) throw new CalcException("Tràn số: kết quả vượt phạm vi double.");
                return new Num(false, 0, v);
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _pos;
            private int _depth;

            public Parser(string s)
            {
                _s = s;
            }

            public Num ParseAll()
            {
                Num value = ParseBitOr();
                SkipWs();
                if (_pos < _s.Length) throw new CalcException($"Ký tự không mong đợi '{_s[_pos]}' ở vị trí {_pos + 1}.");
                return value;
            }

            private void Enter()
            {
                if (++_depth > MaxDepth) throw new CalcException($"Biểu thức lồng quá sâu (tối đa {MaxDepth} mức).");
            }

            private void Leave() => _depth--;

            private void SkipWs()
            {
                while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
            }

            private bool Accept(string token)
            {
                SkipWs();
                if (_pos + token.Length > _s.Length || string.CompareOrdinal(_s, _pos, token, 0, token.Length) != 0) return false;
                _pos += token.Length;
                return true;
            }

            private char Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

            private Num ParseBitOr()
            {
                Num left = ParseBitAnd();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '|') { _pos++; left = BitOp('|', left, ParseBitAnd()); }
                    else return left;
                }
            }

            private Num ParseBitAnd()
            {
                Num left = ParseShift();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '&') { _pos++; left = BitOp('&', left, ParseShift()); }
                    else return left;
                }
            }

            private Num ParseShift()
            {
                Num left = ParseAdditive();
                while (true)
                {
                    if (Accept("<<")) left = BitOp('<', left, ParseAdditive());
                    else if (Accept(">>")) left = BitOp('>', left, ParseAdditive());
                    else return left;
                }
            }

            private Num ParseAdditive()
            {
                Num left = ParseTerm();
                while (true)
                {
                    SkipWs();
                    char c = Peek();
                    if (c == '+') { _pos++; left = Add(left, ParseTerm()); }
                    else if (c == '-') { _pos++; left = Subtract(left, ParseTerm()); }
                    else return left;
                }
            }

            private Num ParseTerm()
            {
                Num left = ParseUnary();
                while (true)
                {
                    SkipWs();
                    char c = Peek();
                    if (c == '*' && Peek(1) != '*') { _pos++; left = Multiply(left, ParseUnary()); }
                    else if (c == '/') { _pos++; left = Divide(left, ParseUnary()); }
                    else if (c == '%') { _pos++; left = Modulo(left, ParseUnary()); }
                    else return left;
                }
            }

            private Num ParseUnary()
            {
                SkipWs();
                char c = Peek();
                if (c == '+' || c == '-')
                {
                    _pos++;
                    Enter();
                    Num operand = ParseUnary();
                    Leave();
                    return c == '+' ? operand : Negate(operand);
                }
                return ParsePower();
            }

            private Num ParsePower()
            {
                Num baseValue = ParsePrimary();
                SkipWs();
                if (Peek() == '^' || (Peek() == '*' && Peek(1) == '*'))
                {
                    _pos += Peek() == '^' ? 1 : 2;
                    Enter();
                    Num exponent = ParseUnary(); // right-associative; allows 2^-1
                    Leave();
                    return Power(baseValue, exponent);
                }
                return baseValue;
            }

            private Num ParsePrimary()
            {
                SkipWs();
                if (_pos >= _s.Length) throw new CalcException("Biểu thức kết thúc giữa chừng (thiếu toán hạng).");
                char c = _s[_pos];

                if (c == '(')
                {
                    _pos++;
                    Enter();
                    Num inner = ParseBitOr();
                    Leave();
                    if (!Accept(")")) throw new CalcException("Thiếu dấu ')'.");
                    return inner;
                }

                if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1)))) return ParseNumber();
                if (char.IsLetter(c) || c == '_') return ParseIdentifier();

                throw new CalcException($"Ký tự không mong đợi '{c}' ở vị trí {_pos + 1}.");
            }

            private Num ParseNumber()
            {
                int start = _pos;
                if (_s[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X')) return ParseRadix(16, 2, "Hex");
                if (_s[_pos] == '0' && (Peek(1) == 'b' || Peek(1) == 'B')) return ParseRadix(2, 2, "Bin");

                bool real = false;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                if (Peek() == '.')
                {
                    real = true;
                    _pos++;
                    while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                }
                if ((Peek() == 'e' || Peek() == 'E') &&
                    (char.IsDigit(Peek(1)) || ((Peek(1) == '+' || Peek(1) == '-') && char.IsDigit(Peek(2)))))
                {
                    real = true;
                    _pos += 2;
                    while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                }
                RejectGlued();

                string text = _s.Substring(start, _pos - start);
                if (!real && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long integer)) return Num.Int(integer);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return Num.Real(d);
                throw new CalcException($"Số không hợp lệ '{text}'.");
            }

            private Num ParseRadix(int radix, int prefixLength, string name)
            {
                _pos += prefixLength;
                int digitsStart = _pos;
                ulong value = 0;
                while (_pos < _s.Length)
                {
                    int digit = DigitValue(_s[_pos]);
                    if (digit < 0 || digit >= radix) break;
                    if (value > (ulong.MaxValue - (ulong)digit) / (ulong)radix) throw new CalcException($"Hằng số {name} vượt phạm vi Int64.");
                    value = value * (ulong)radix + (ulong)digit;
                    _pos++;
                }
                if (_pos == digitsStart) throw new CalcException($"Thiếu chữ số sau tiền tố {name} ({(radix == 16 ? "0x" : "0b")}).");
                RejectGlued();
                if (value > long.MaxValue) throw new CalcException($"Hằng số {name} vượt phạm vi Int64.");
                return Num.Int((long)value);
            }

            /// <summary>"2pi", "0xFG", "12abc": no implicit multiplication and no stray letters after a number.</summary>
            private void RejectGlued()
            {
                char next = Peek();
                if (char.IsLetterOrDigit(next) || next == '_' || next == '.')
                    throw new CalcException($"Số viết liền ký tự '{next}' ở vị trí {_pos + 1} (dùng toán tử, ví dụ 2*pi).");
            }

            private static int DigitValue(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            private Num ParseIdentifier()
            {
                int start = _pos;
                while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
                string name = _s.Substring(start, _pos - start).ToLowerInvariant();

                SkipWs();
                if (Peek() != '(')
                {
                    return name switch
                    {
                        "pi" => Num.Real(Math.PI),
                        "e" => Num.Real(Math.E),
                        _ => throw new CalcException($"Không biết hằng số '{name}' (có: pi, e).")
                    };
                }

                _pos++; // '('
                Enter();
                var args = new List<Num>(2);
                SkipWs();
                if (Peek() != ')')
                {
                    while (true)
                    {
                        args.Add(ParseBitOr());
                        if (Accept(",")) continue;
                        break;
                    }
                }
                if (!Accept(")")) throw new CalcException($"Thiếu dấu ')' sau tham số của {name}(...).");
                Leave();
                return Call(name, args);
            }

            private static Num Call(string name, List<Num> a)
            {
                void Arity(int n)
                {
                    if (a.Count != n) throw new CalcException($"{name}() cần {n} tham số, nhận {a.Count}.");
                }

                switch (name)
                {
                    case "sqrt": Arity(1); return Num.Real(Math.Sqrt(a[0].AsDouble));
                    case "abs":
                        Arity(1);
                        if (a[0].IsInt && a[0].I != long.MinValue) return Num.Int(Math.Abs(a[0].I));
                        return Num.Real(Math.Abs(a[0].AsDouble));
                    case "round": Arity(1); return Integral(a[0], v => Math.Round(v, MidpointRounding.AwayFromZero));
                    case "floor": Arity(1); return Integral(a[0], Math.Floor);
                    case "ceil": Arity(1); return Integral(a[0], Math.Ceiling);
                    case "min":
                    case "max":
                    {
                        if (a.Count < 2) throw new CalcException($"{name}() cần ít nhất 2 tham số.");
                        Num best = a[0];
                        for (int i = 1; i < a.Count; i++)
                        {
                            bool better = name == "min" ? Compare(a[i], best) < 0 : Compare(a[i], best) > 0;
                            if (better) best = a[i];
                        }
                        return best;
                    }
                    case "pow": Arity(2); return Power(a[0], a[1]);
                    case "log":
                    case "ln": Arity(1); return Num.Real(Math.Log(a[0].AsDouble));
                    case "log2": Arity(1); return Num.Real(Math.Log2(a[0].AsDouble));
                    case "log10": Arity(1); return Num.Real(Math.Log10(a[0].AsDouble));
                    case "sin": Arity(1); return Num.Real(Math.Sin(a[0].AsDouble));
                    case "cos": Arity(1); return Num.Real(Math.Cos(a[0].AsDouble));
                    case "tan": Arity(1); return Num.Real(Math.Tan(a[0].AsDouble));
                    default:
                        throw new CalcException($"Không biết hàm '{name}' (có: sqrt abs round floor ceil min max pow log ln log2 log10 sin cos tan).");
                }
            }

            private static Num Integral(Num x, Func<double, double> op)
            {
                if (x.IsInt) return x;
                return Num.Real(op(x.D));
            }

            private static int Compare(Num x, Num y) =>
                x.IsInt && y.IsInt ? x.I.CompareTo(y.I) : x.AsDouble.CompareTo(y.AsDouble);

            private static Num Negate(Num x)
            {
                if (x.IsInt && x.I != long.MinValue) return Num.Int(-x.I);
                return Num.Real(-x.AsDouble);
            }

            private static Num Add(Num x, Num y)
            {
                if (x.IsInt && y.IsInt)
                {
                    try { return Num.Int(checked(x.I + y.I)); }
                    catch (OverflowException) { }
                }
                return Num.Real(x.AsDouble + y.AsDouble);
            }

            private static Num Subtract(Num x, Num y)
            {
                if (x.IsInt && y.IsInt)
                {
                    try { return Num.Int(checked(x.I - y.I)); }
                    catch (OverflowException) { }
                }
                return Num.Real(x.AsDouble - y.AsDouble);
            }

            private static Num Multiply(Num x, Num y)
            {
                if (x.IsInt && y.IsInt)
                {
                    try { return Num.Int(checked(x.I * y.I)); }
                    catch (OverflowException) { }
                }
                return Num.Real(x.AsDouble * y.AsDouble);
            }

            private static Num Divide(Num x, Num y)
            {
                if (y.AsDouble == 0) throw new CalcException("Chia cho 0.");
                if (x.IsInt && y.IsInt && !(x.I == long.MinValue && y.I == -1) && x.I % y.I == 0) return Num.Int(x.I / y.I);
                return Num.Real(x.AsDouble / y.AsDouble);
            }

            private static Num Modulo(Num x, Num y)
            {
                if (y.AsDouble == 0) throw new CalcException("Chia lấy dư cho 0.");
                if (x.IsInt && y.IsInt) return Num.Int(y.I == -1 ? 0 : x.I % y.I);
                return Num.Real(x.AsDouble % y.AsDouble);
            }

            private static Num Power(Num x, Num y)
            {
                if (x.IsInt && y.IsInt && y.I >= 0)
                {
                    try
                    {
                        long result = 1, b = x.I, e = y.I;
                        while (e > 0)
                        {
                            if ((e & 1) == 1) result = checked(result * b);
                            e >>= 1;
                            if (e > 0) b = checked(b * b);
                        }
                        return Num.Int(result);
                    }
                    catch (OverflowException) { }
                }
                return Num.Real(Math.Pow(x.AsDouble, y.AsDouble));
            }

            private static long ToInt64ForBits(Num x, char op)
            {
                if (x.IsInt) return x.I;
                double d = x.D;
                if (Math.Floor(d) == d && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18) return (long)d;
                throw new CalcException($"Phép '{OpName(op)}' cần số nguyên Int64 (nhận {d.ToString("G15", CultureInfo.InvariantCulture)}).");
            }

            private static string OpName(char op) => op switch { '<' => "<<", '>' => ">>", _ => op.ToString() };

            private static Num BitOp(char op, Num x, Num y)
            {
                long a = ToInt64ForBits(x, op);
                long b = ToInt64ForBits(y, op);
                switch (op)
                {
                    case '&': return Num.Int(a & b);
                    case '|': return Num.Int(a | b);
                    default:
                        if (b < 0 || b > 63) throw new CalcException($"Số bit dịch phải trong 0..63 (nhận {b}).");
                        return Num.Int(op == '<' ? a << (int)b : a >> (int)b);
                }
            }
        }
    }
}
