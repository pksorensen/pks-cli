---
allowed-tools: mcp__github__list_notifications, mcp__github__list_issues, mcp__github__list_pull_requests, mcp__github__search_issues, mcp__github__search_pull_requests, mcp__github__get_me, mcp__github__get_issue, mcp__github__get_pull_request, mcp__github__get_notification_details, mcp__github__dismiss_notification, mcp__github__mark_all_notifications_read, mcp__github__manage_notification_subscription
description: Triage GitHub notifications, issues, and pull requests
argument-hint: (optional) repository owner/name or filter type (notifications, issues, prs, review-requests)
---

# GitHub Triage Assistant

I'll help you triage your GitHub notifications, issues, and pull requests efficiently.

## Arguments: $ARGUMENTS

## Task

Based on the provided arguments (or lack thereof), I will:

1. **No arguments**: Show a comprehensive overview of all your GitHub activity including:

   - Unread notifications (grouped by repository)
   - Open issues assigned to you
   - Open pull requests you authored
   - Pull requests awaiting your review
   - Recent mentions

2. **Repository specified** (e.g., "owner/repo"): Focus the triage on a specific repository:

   - Repository-specific notifications
   - Repository issues assigned to you
   - Repository PRs you're involved with

3. **Filter type specified**:
   - `notifications`: Focus on unread notifications with quick actions
   - `issues`: Show and help prioritize your assigned issues
   - `prs` or `pull-requests`: Focus on your pull requests
   - `review-requests`: Show PRs awaiting your review

## Process

I will:

1. Fetch the relevant GitHub data based on your request
2. Organize and present the information in a clear, actionable format
3. Highlight urgent items (e.g., security alerts, failing CI, review requests)
4. Suggest prioritization based on:
   - Age of the item
   - Number of participants/comments
   - Labels (bug, security, urgent, etc.)
   - CI/build status
5. Offer quick actions like:
   - Marking notifications as read
   - Viewing specific issues/PRs in detail
   - Dismissing or muting certain notifications

## Interactive Options

After presenting the triage summary, I'll offer to:

- Dive deeper into specific items
- Mark groups of notifications as read
- Help compose responses to issues/PRs
- Create follow-up tasks or reminders
- Generate a daily/weekly report

Let me analyze your GitHub activity now...
