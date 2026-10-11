namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitResult CreateTag(string name, string commit, string message)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0 || name.StartsWith("-") || name == "HEAD" ||
                !Run("check-ref-format", TagRefs + name).IsSuccess)
            {
                return GitResult.Failure($"'{name}' is not a valid tag name");
            }

            if (Run("rev-parse", "-q", "--verify", TagRefs + name).IsSuccess)
            {
                return GitResult.Failure($"Tag {name} already exists");
            }

            var done = $"Tag {name} created on {commit}";
            if (string.IsNullOrWhiteSpace(message))
            {
                var created = Run("update-ref", TagRefs + name, commit + "^{commit}", "");
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

        public GitResult DeleteTag(string name, string remote)
        {
            var note = "";
            var removedOnRemote = false;
            if (remote != null)
            {
                var deleted = Run("push", "--no-follow-tags", "--delete", remote, TagRefs + name);
                if (!deleted.IsSuccess)
                {
                    return GitResult.Failure($"Tag {name} not deleted: deleting it on {remote} failed\n{deleted.Message}");
                }

                removedOnRemote = !deleted.Error.Contains("non-existent ref");
                note = removedOnRemote ? $" here and on {remote}" : $", it was not on {remote}";
            }

            var local = Run("update-ref", "-d", TagRefs + name);
            if (!local.IsSuccess)
            {
                return removedOnRemote
                    ? GitResult.Done($"Tag {name} deleted on {remote}, but not here\n{local.Message}")
                    : local;
            }

            return GitResult.Success($"Tag {name} deleted{note}");
        }
    }
}
