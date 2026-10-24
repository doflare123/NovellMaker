using MoonSharp.Interpreter;

namespace NovellMaker.Core.Scripting;

/// <summary>One stream shared by a component's files. All native sampling loops consume the invocation quota.</summary>
internal sealed class LuaRandomApi(LuaStandardLibrary library)
{
    internal const int MaximumDraws = 1024;
    private ulong value = LuaRandomState.Initial.Value;
    private int draws;
    internal bool Changed { get; private set; }
    internal LuaRandomState State => LuaRandomState.Capture(value);

    internal void Begin(LuaRandomState? state = null)
    {
        if (state is not null) value = state.Value;
        draws = 0; Changed = false;
    }

    private uint Next()
    {
        if (draws >= MaximumDraws) throw new ScriptRuntimeException("Превышен бюджет RNG (1024 выборки за обработчик).");
        library.Charge(1); draws++; Changed = true;
        return LuaRandomState.Step(ref value);
    }

    internal void Install(Table ui, Func<bool> handling)
    {
        void Bind(string name, int minimum, int maximum, Func<CallbackArguments, DynValue> callback) =>
            ui.Set(name, DynValue.NewCallback((_, args) =>
            {
                if (!handling()) throw new ScriptRuntimeException("RNG доступен только в обработчике.");
                if (args.Count < minimum || args.Count > maximum) throw new ScriptRuntimeException($"{name}: неверное количество аргументов.");
                library.Charge(1); return callback(args);
            }));
        static double Integer(DynValue input, double minimum, double maximum)
        {
            if (input.Type != DataType.Number || !double.IsFinite(input.Number) || input.Number < minimum || input.Number > maximum || input.Number != Math.Truncate(input.Number))
                throw new ScriptRuntimeException("RNG: ожидалось целое число в разрешённом диапазоне.");
            return input.Number;
        }
        Bind("seed_random", 1, 1, args =>
        {
            var seed = (uint)Integer(args[0], 0, uint.MaxValue);
            // Reseeding performs two PCG steps but cannot replenish the invocation quota.
            if (draws > MaximumDraws - 2) throw new ScriptRuntimeException("Превышен бюджет RNG (1024 выборки за обработчик).");
            library.Charge(2); draws += 2; value = LuaRandomState.FromSeed(seed).Value; Changed = true; return DynValue.Nil;
        });
        Bind("get_random_state", 0, 0, _ =>
        {
            library.ReserveText(25); return DynValue.NewString(State.ToString());
        });
        Bind("set_random_state", 1, 1, args =>
        {
            if (args[0].Type != DataType.String) throw new ScriptRuntimeException("Снимок RNG должен быть строкой.");
            LuaRandomState next;
            try { next = LuaRandomState.Parse(args[0].String); }
            catch (ArgumentException error) { throw new ScriptRuntimeException(error.Message); }
            value = next.Value; Changed = true; return DynValue.Nil;
        });
        Bind("random", 0, 2, args =>
        {
            if (args.Count == 0)
                return DynValue.NewNumber(Next() / 4294967296d); // Exactly representable multiples of 2^-32, [0,1).
            var lower = args.Count == 1 ? 1L : (long)Integer(args[0], int.MinValue, int.MaxValue);
            var upper = (long)Integer(args[args.Count - 1], int.MinValue, int.MaxValue);
            if (lower > upper) throw new ScriptRuntimeException("RNG: нижняя граница больше верхней.");
            var width = (ulong)(upper - lower + 1);
            if (width == 1UL << 32) return DynValue.NewNumber(lower + Next());
            var bound = (uint)width;
            var threshold = unchecked(0u - bound) % bound;
            while (true)
            {
                var sample = Next();
                if (sample >= threshold) return DynValue.NewNumber(lower + sample % bound);
            }
        });
    }
}
