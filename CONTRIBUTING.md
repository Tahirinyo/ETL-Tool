# Contributing

This MVP was planned for two developers and uses a lightweight, task-based workflow. Keep `main` stable and limit each branch to one approved task.

## Task workflow

1. Start from the latest `main`.
2. Create a lowercase kebab-case branch:
   - `feature/task-<number>-<description>` for planned work.
   - `fix/task-<number>-<description>` for corrections.
3. Implement only the approved task and its relevant tests.
4. Run the solution checks:

   ```text
   dotnet restore EtlTool.sln
   dotnet build EtlTool.sln --no-restore
   dotnet test EtlTool.sln --no-build
   ```

5. Complete a Codex review and review the diff yourself.
6. Open a pull request to `main` and wait for green CI.
7. Squash merge the pull request and delete the task branch.

Do not create `develop`, release, hotfix, or person-based branches. The Developer A and Developer B labels in the project plan are historical role assignments, not branch names.
