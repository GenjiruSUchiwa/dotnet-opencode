// Generated from packages/core/src/plugin/provider/openai.ts; no model identities are embedded here.
namespace OpenCode.Core.Llm;
internal static class OpenAiOAuthPolicyData {
internal const string SourceSha256 = "d366e23ddd07065c83e8622d53740b20eb655c22fb96b0268d096cd42ee0c5f7";
internal const double LegacyVersionCeiling = 5.4d;
internal const double ContextLimit = 400000d;
internal const double InputLimit = 272000d;
internal static bool Allowed(string hash) => hash is "ffc50c70661c227edf8daae6f8dbed2dd0645386c12d43bc7fc44da166e043bd" or "aa0caa1cd971721de2287570ac837c8f24ce12bb5d9346c4144f3f7a3e224b13" or "2a7b79b0151aa44a0abee17adc0e18df1c07d8d15d7affa989c3b3afb6bee0a0" or "d416b3a370c3fd5c7a1f98932bed7b394e0f536653bd221f53fe6702e32d725f";
internal static bool Denied(string hash) => hash is "6628809d3f3e28c3106e95f1a20e072a0733059f8e26976da4efe14b0894a255" or "b2eb13c61b299154bbc8af7301698bc457b8e948d08543a9945c9315536ee5b8";
}
