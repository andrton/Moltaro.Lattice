# Identity specification tests

The four `.cs` files in this folder are the shared test suite that Microsoft uses to check
an ASP.NET Core Identity store: about 110 tests through `UserManager` and `RoleManager`.
They are copied here unchanged, because the suite is not published as a current NuGet
package (the latest `Microsoft.AspNetCore.Identity.Specification.Tests` on nuget.org is
5.0.17).

| | |
| --- | --- |
| Source | <https://github.com/dotnet/aspnetcore>, `src/Identity/Specification.Tests/src` |
| Version | tag `v10.0.15`, commit `547fc16b976e18911f9df7630f1f75758ab248f2` |
| License | MIT, © .NET Foundation and Contributors; see [LICENSE.txt](LICENSE.txt) |
| Local changes | None |

Rules for this folder:

- The files stay as published. They are not formatted or analyzed as code of this
  repository; the root `.editorconfig` marks them as generated for that reason.
- To update, replace all four files with those of a newer tag and change the version above.
- The tests are run against the Lattice stores by `LatticeSpecificationTests` in the parent
  folder. A test that fails is fixed in the stores, never here.
