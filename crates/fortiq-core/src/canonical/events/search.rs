//! Local in-memory search index over decrypted ticket states.
//!
//! Specifications (docs/spec/19-snapshots-search-cold-start.md):
//! - No unencrypted global search index written to disk.
//! - Search index is constructed in local memory after decryption.
//! - Enables fast lookup across ticket titles, message bodies, and attachment names.

use crate::canonical::events::reducer::TicketView;
use crate::canonical::types::TicketId;

/// Classification of a search match.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MatchType {
    Title,
    MessageBody,
    AttachmentFilename,
}

/// A search result returned by the index.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchResult {
    pub ticket_id: TicketId,
    pub match_type: MatchType,
    pub snippet: String,
}

/// In-memory search index for sovereign client and operator instances.
#[derive(Default, Debug, Clone)]
pub struct LocalSearchIndex {
    tickets: Vec<TicketView>,
}

impl LocalSearchIndex {
    pub fn new() -> Self {
        Self::default()
    }

    /// Indexes or updates a materialized ticket view in memory.
    pub fn index_ticket(&mut self, view: TicketView) {
        if let Some(pos) = self
            .tickets
            .iter()
            .position(|t| t.ticket_id == view.ticket_id)
        {
            self.tickets[pos] = view;
        } else {
            self.tickets.push(view);
        }
    }

    /// Searches indexed tickets using a case-insensitive query string.
    pub fn search(&self, query: &str) -> Vec<SearchResult> {
        let query_lower = query.trim().to_lowercase();
        if query_lower.is_empty() {
            return Vec::new();
        }

        let mut results = Vec::new();

        for ticket in &self.tickets {
            // Check ticket title
            if ticket.title.to_lowercase().contains(&query_lower) {
                results.push(SearchResult {
                    ticket_id: ticket.ticket_id,
                    match_type: MatchType::Title,
                    snippet: ticket.title.clone(),
                });
            }

            // Check chat messages
            for msg in &ticket.messages {
                if msg.body.to_lowercase().contains(&query_lower) {
                    results.push(SearchResult {
                        ticket_id: ticket.ticket_id,
                        match_type: MatchType::MessageBody,
                        snippet: msg.body.clone(),
                    });
                }
            }

            // Check attachment filenames
            for att in &ticket.attachments {
                if att.filename.to_lowercase().contains(&query_lower) {
                    results.push(SearchResult {
                        ticket_id: ticket.ticket_id,
                        match_type: MatchType::AttachmentFilename,
                        snippet: att.filename.clone(),
                    });
                }
            }
        }

        results
    }
}
