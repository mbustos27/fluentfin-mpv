// Builds FluentFin.Core types whatever their constructor looks like in the Core under test (upstream FluentFin, this
// fork's baseline, or the working tree): every parameter is filled from the instances passed in, a NullLogger, a Moq
// mock of its interface, or (a class of FluentFin.Core) one built the same way. So one Program.cs builds against all of
// them. KnownFolders is never built here: its default constructor is the user's real data folder; pass one.
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

static class CoreFactory
{
	public static T Make<T>(params object[] known) => (T)Make(typeof(T), known);

	public static object Make(Type type, params object[] known)
	{
		var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
		var args = ctor.GetParameters().Select(p => Fill(p.ParameterType, known)).ToArray();
		return ctor.Invoke(args);
	}

	private static object? Fill(Type t, object[] known)
	{
		if (known.FirstOrDefault(t.IsInstanceOfType) is { } k)
		{
			return k;
		}
		if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>))
		{
			return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(t.GetGenericArguments()));
		}
		if (t.IsInterface || t.IsAbstract)
		{
			var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(t))!;
			return mock.Object;
		}
		if (t.Assembly == typeof(FluentFin.Core.KnownFolders).Assembly && t != typeof(FluentFin.Core.KnownFolders) && t.IsClass)
		{
			return Make(t, known);
		}
		throw new InvalidOperationException($"no value for a constructor parameter of type {t}");
	}
}
