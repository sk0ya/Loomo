using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services.GitHub;

/// <summary>チェック（CI）のまとめ。</summary>
public enum GitHubChecksState { None, Pending, Success, Failure }

/// <summary>一覧の1行ぶんの PR。</summary>
public sealed record GitHubPullRequest(
    int Number, string Title, string Author, string HeadRef, string BaseRef, bool IsDraft,
    DateTimeOffset? UpdatedAt, string Url, string ReviewDecision, GitHubChecksState Checks, bool IsCrossRepository);

/// <summary>PR・Issue の会話の1件（コメント／レビュー）。<see cref="State"/> はレビューのときだけ
/// （<c>APPROVED</c> / <c>CHANGES_REQUESTED</c> / <c>COMMENTED</c> …）。</summary>
public sealed record GitHubComment(string Author, string Body, DateTimeOffset? CreatedAt, string? State = null);

/// <summary>PR で変わったファイル。</summary>
public sealed record GitHubChangedFile(string Path, int Additions, int Deletions);

/// <summary>チェック1つ。</summary>
public sealed record GitHubCheck(string Name, GitHubChecksState State, string? Url);

/// <summary>PR の中身（概要・会話・変わったファイル・チェック）。</summary>
public sealed record GitHubPullRequestDetail(
    GitHubPullRequest Summary, string Body, string State, int Additions, int Deletions, string HeadOid,
    string Mergeable, IReadOnlyList<GitHubChangedFile> Files, IReadOnlyList<GitHubComment> Conversation,
    IReadOnlyList<GitHubCheck> CheckRuns);

/// <summary>一覧の1行ぶんの Issue。</summary>
public sealed record GitHubIssue(
    int Number, string Title, string Author, IReadOnlyList<string> Labels, DateTimeOffset? UpdatedAt,
    string Url, int CommentCount, string State);

/// <summary>Issue の中身。</summary>
public sealed record GitHubIssueDetail(GitHubIssue Summary, string Body, IReadOnlyList<GitHubComment> Comments);

/// <summary>レビューの種類（<c>gh pr review</c> の3つ）。</summary>
public enum GitHubReviewKind { Approve, RequestChanges, Comment }
