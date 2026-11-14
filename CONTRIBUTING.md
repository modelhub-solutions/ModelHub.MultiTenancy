# Contributing

Thanks for helping. Small, focused pull requests are easiest to review.

1. Open an issue (or comment on an existing one) describing the change before starting larger work.
2. Fork, create a branch, and make the change with tests.
3. Run `dotnet test` — the suite runs on .NET 8 and .NET 10.
4. Open a pull request that links the issue (`Closes #123`).

Issues labelled **good first issue** are scoped for a first contribution.

## Ground rules

- Tenant isolation is the point of this library: any change to filtering, impersonation or the save interceptor needs a test that proves data can't cross tenants.
- Keep the public API small. New options need a sensible, safe default.
- Be kind. We follow the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).
