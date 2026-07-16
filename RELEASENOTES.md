# v0.15

- Skip workflow creation when the source repo has no secrets to migrate. The tool now
  lists the source repo's Actions secrets first and exits early — no migration branch,
  no workflow file, and no Actions run — when there is nothing to migrate.
