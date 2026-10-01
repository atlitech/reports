// No test may hang the run. Starting the AppHost, which the first tests wait for, includes building
// the reports server image (three to four minutes on a GitHub runner without a build cache), so the
// limit is generous.
[assembly: Timeout(900_000)]
