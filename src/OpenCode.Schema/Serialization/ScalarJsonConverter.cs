namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Wire metadata shared by compatibility codecs and native OpenAPI export.</summary>
public interface IScalarJsonConverter
{
    Type ScalarType { get; }
}

/// <summary>Dictionary keys use the same factory/primitive codec as scalar values.</summary>
public abstract class ScalarJsonConverter<T, TScalar>(Func<TScalar, T> create, Func<T, TScalar> unwrap)
    : JsonConverter<T>, IScalarJsonConverter where TScalar : notnull
{
    public Type ScalarType => typeof(TScalar);

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        create(((JsonConverter<TScalar>)options.GetConverter(typeof(TScalar))).ReadAsPropertyName(ref reader, typeof(TScalar), options));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        ((JsonConverter<TScalar>)options.GetConverter(typeof(TScalar))).WriteAsPropertyName(writer, unwrap(value), options);
}
