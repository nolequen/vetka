namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitResult CreateTag(string name, string commit, string message)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0 || name.StartsWith("-") || name == "HEAD" ||
                !Run("check-ref-format", "refs/tags/" + name).IsSuccess)
            {
                return GitResult.Failure($"'{name}' is not a valid tag name");
            }

            if (Run("rev-parse", "-q", "--verify", "refs/tags/" + name).IsSuccess)
            {
                return GitResult.Failure($"Tag {name} already exists");
            }

            var done = $"Tag {name} created on {commit}";
            if (string.IsNullOrWhiteSpace(message))
            {
                var created = Run("update-ref", "refs/tags/" + name, commit + "^{commit}", "");
                return created.IsSuccess ? GitResult.Success(done) : created;
            }

            string messageFile = null;
            try
            {
                messageFile = WriteTempFile(message);
                var annotated = Run("tag", "--annotate", "--cleanup=whitespace", "--file", messageFile, name, commit);
                return annotated.IsSuccess ? GitResult.Success(done) : annotated;
            }
            finally
            {
                DeleteTempFile(messageFile);
            }
        }

        public GitResult DeleteTag(string name, bool onRemote)
        {
            string remote = null;
            var note = "";
            if (onRemote)
            {
                remote = TagRemote(out var problem);
                if (remote == null)
                {
                    return GitResult.Failure($"Tag {name} not deleted: {problem}");
                }

                var deleted = Interruptible("Deletion",
                    () => Run("push", "--no-follow-tags", "--delete", remote, "refs/tags/" + name));
                if (deleted.IsCancelled)
                {
                    return GitResult.Cancelled($"Deletion of tag {name} cancelled");
                }

                if (!deleted.IsSuccess)
                {
                    return GitResult.Failure($"Tag {name} not deleted: deleting it on {remote} failed\n{deleted.Message}");
                }

                note = deleted.Error.Contains("non-existent ref") ? $", it was not on {remote}" : $" here and on {remote}";
            }

            var local = Run("update-ref", "-d", "refs/tags/" + name);
            if (!local.IsSuccess)
            {
                return remote != null && !note.StartsWith(",")
                    ? GitResult.Failure($"Tag {name} deleted on {remote}, but not here\n{local.Message}")
                    : local;
            }

            return GitResult.Success($"Tag {name} deleted{note}");
        }
    }
}
