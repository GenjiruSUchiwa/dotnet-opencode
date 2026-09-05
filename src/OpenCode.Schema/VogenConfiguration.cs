using Vogen;

// Keep wire/error adapters explicit. Vogen owns storage, initialization and typed
// equality; the existing JSON codecs retain the public wire validation contract.
[assembly: VogenDefaults(conversions: Conversions.None,
    toPrimitiveCasting: CastOperator.None,
    fromPrimitiveCasting: CastOperator.None,
    primitiveEqualityGeneration: PrimitiveEqualityGeneration.Omit)]
