namespace OpenCode.Core.Tools;

using OpenCode.Core.Permissions;

/// <summary>Grant-prefix arities from shell/parse.ts; raw words preserve the source permission vocabulary.</summary>
internal static class ShellCommandPrefix
{
    private static readonly HashSet<string> Two = new(StringComparer.Ordinal)
    {
        "bazel", "brew", "bun", "cargo", "cdk", "cf", "cmake", "composer", "consul", "crictl", "deno", "docker",
        "eksctl", "firebase", "flyctl", "git", "go", "gradle", "helm", "heroku", "hugo", "ip", "kind", "kubectl",
        "kustomize", "make", "mc", "minikube", "mongosh", "mysql", "mvn", "ng", "npm", "npx", "nvm", "nx", "openssl",
        "pip", "pipenv", "pnpm", "poetry", "podman", "psql", "pulumi", "python", "pyenv", "rake", "rbenv", "redis-cli",
        "rustup", "serverless", "skaffold", "sls", "sst", "swift", "systemctl", "terraform", "tmux", "turbo", "ufw",
        "vault", "vercel", "volta", "wp", "yarn"
    };
    private static readonly HashSet<string> Three = new(StringComparer.Ordinal)
    {
        "aws", "az", "doctl", "gcloud", "gh", "sfdx", "bun run", "bun x", "cargo add", "cargo run", "consul kv",
        "deno task", "docker builder", "docker compose", "docker container", "docker image", "docker network",
        "docker volume", "eksctl create", "git config", "git remote", "git stash", "ip addr", "ip link", "ip netns",
        "ip route", "kind create", "kubectl kustomize", "kubectl rollout", "mc admin", "npm exec", "npm init", "npm run",
        "npm view", "openssl req", "openssl x509", "pnpm dlx", "pnpm exec", "pnpm run", "podman container", "podman image",
        "pulumi stack", "terraform workspace", "vault auth", "vault kv", "yarn dlx", "yarn run"
    };

    public static string Save(IReadOnlyList<string> words)
    {
        var arity = Three.Contains(string.Join(' ', words.Take(2))) || Three.Contains(words[0]) ? 3 : Two.Contains(words[0]) ? 2 : 1;
        return string.Join(' ', words.Take(arity)) + " *";
    }

    public static string? Grant(ScannedShellCommand command)
    {
        if (command.Words.Count == 0) return command.Resource.IndexOfAny(['*', '?']) < 0 ? command.Resource : null;
        var conventional = Save(command.Words.Take(command.PrefixWordCount ?? command.Words.Count).Select(word => word.Raw).ToArray());
        // The permission wildcard algebra cannot escape literal '*'/'?'. Never
        // turn an assignment/substitution containing these into a broad saved grant.
        if (conventional[..^2].IndexOfAny(['*', '?']) >= 0) return null;
        if (PermissionRules.Match(command.Resource, conventional)) return conventional;
        // Assignment prefixes, call operators, quoting and unusual whitespace must
        // not be dropped to manufacture a grant for a different resource.
        return command.Resource.IndexOfAny(['*', '?']) < 0 ? command.Resource : null;
    }
}
