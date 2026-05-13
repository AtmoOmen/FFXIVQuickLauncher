using System.Text;

namespace XIVLauncher.Common;

public enum Repository
{
    Boot,
    Ffxiv,
    Ex1,
    Ex2,
    Ex3,
    Ex4,
    Ex5
}

public static class RepoExtensions
{
    extension(Repository repo)
    {
        public FileInfo GetVerFile(DirectoryInfo gamePath, bool isBck = false)
        {
            var repoPath = repo.GetRepoPath(gamePath).FullName;

            return repo switch
            {
                Repository.Boot  => new FileInfo(Path.Combine(repoPath, "ffxivboot" + (isBck ? ".bck" : ".ver"))),
                Repository.Ffxiv => new FileInfo(Path.Combine(repoPath, "ffxivgame" + (isBck ? ".bck" : ".ver"))),
                Repository.Ex1   => new FileInfo(Path.Combine(repoPath, "ex1"       + (isBck ? ".bck" : ".ver"))),
                Repository.Ex2   => new FileInfo(Path.Combine(repoPath, "ex2"       + (isBck ? ".bck" : ".ver"))),
                Repository.Ex3   => new FileInfo(Path.Combine(repoPath, "ex3"       + (isBck ? ".bck" : ".ver"))),
                Repository.Ex4   => new FileInfo(Path.Combine(repoPath, "ex4"       + (isBck ? ".bck" : ".ver"))),
                Repository.Ex5   => new FileInfo(Path.Combine(repoPath, "ex5"       + (isBck ? ".bck" : ".ver"))),
                _                => throw new ArgumentOutOfRangeException(nameof(repo), repo, null)
            };
        }

        public string GetVer(DirectoryInfo gamePath, bool isBck = false)
        {
            var verFile = repo.GetVerFile(gamePath, isBck);

            if (!verFile.Exists)
                return Constants.BASE_GAME_VERSION;

            var ver = File.ReadAllText(verFile.FullName);
            return string.IsNullOrWhiteSpace(ver) ? Constants.BASE_GAME_VERSION : ver;
        }

        public void SetVer(DirectoryInfo gamePath, string newVer, bool isBck = false)
        {
            var verFile = repo.GetVerFile(gamePath, isBck);

            if (verFile.Directory != null && !verFile.Directory.Exists)
                verFile.Directory.Create();

            using var fileStream = verFile.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            var       buffer     = Encoding.ASCII.GetBytes(newVer);
            fileStream.Write(buffer, 0, buffer.Length);
            fileStream.Flush();
        }

        public bool IsBaseVer(DirectoryInfo gamePath) =>
            repo.GetVer(gamePath) == Constants.BASE_GAME_VERSION;

        private DirectoryInfo GetRepoPath(DirectoryInfo gamePath) =>
            repo switch
            {
                Repository.Boot  => new DirectoryInfo(Path.Combine(gamePath.FullName, "boot")),
                Repository.Ffxiv => new DirectoryInfo(Path.Combine(gamePath.FullName, "game")),
                Repository.Ex1   => new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex1")),
                Repository.Ex2   => new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex2")),
                Repository.Ex3   => new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex3")),
                Repository.Ex4   => new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex4")),
                Repository.Ex5   => new DirectoryInfo(Path.Combine(gamePath.FullName, "game", "sqpack", "ex5")),
                _                => throw new ArgumentOutOfRangeException(nameof(repo), repo, null)
            };
    }
}
