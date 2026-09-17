//! EventPack batcher and flush policy.
//!
//! Separates logical event granularity from cryptographic/storage granularity
//! by accumulating small chat/state events into a bounded EventPack.
//! Immediate safety events (client access revoke, ticket close) bypass delay
//! and flush immediately.

use crate::canonical::records::{EventPackPlaintext, LogicalEvent};
use crate::canonical::types::TicketId;
use rand_core::{OsRng, RngCore};
use std::time::Instant;

/// Decision returned when an event is added to the batcher.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FlushDecision {
    /// The event was buffered; batcher is waiting for more events or timeout.
    Buffered,
    /// A threshold was reached or an immediate safety event requires immediate flush.
    FlushImmediately,
}

/// Flush configuration policy.
#[derive(Debug, Clone)]
pub struct BatchPolicy {
    pub max_events: usize,
    pub max_delay_ms: u64,
    pub target_plaintext_bytes: usize,
    pub hard_max_bytes: usize,
}

impl Default for BatchPolicy {
    fn default() -> Self {
        Self {
            max_events: 32,
            max_delay_ms: 150,
            target_plaintext_bytes: 64 * 1024,
            hard_max_bytes: 256 * 1024,
        }
    }
}

/// Bounded accumulator that batches events into an EventPackPlaintext.
pub struct EventPackBatcher {
    ticket_id: Option<TicketId>,
    ticket_crypto_epoch: Option<u64>,
    policy: BatchPolicy,
    buffered_events: Vec<LogicalEvent>,
    first_event_at: Option<Instant>,
}

impl EventPackBatcher {
    pub fn new(ticket_id: Option<TicketId>, ticket_crypto_epoch: Option<u64>) -> Self {
        Self::with_policy(ticket_id, ticket_crypto_epoch, BatchPolicy::default())
    }

    pub fn with_policy(
        ticket_id: Option<TicketId>,
        ticket_crypto_epoch: Option<u64>,
        policy: BatchPolicy,
    ) -> Self {
        Self {
            ticket_id,
            ticket_crypto_epoch,
            policy,
            buffered_events: Vec::new(),
            first_event_at: None,
        }
    }

    /// Determines if an event is an immediate safety event that must not be delayed.
    pub fn is_immediate_safety_event(event: &LogicalEvent) -> bool {
        matches!(
            event,
            LogicalEvent::TicketStateChanged { new_state, .. } if *new_state == 3 || *new_state == 4
        )
    }

    /// Pushes an event into the batcher and returns whether it needs an immediate flush.
    pub fn push(&mut self, event: LogicalEvent) -> FlushDecision {
        let is_safety = Self::is_immediate_safety_event(&event);

        if self.first_event_at.is_none() {
            self.first_event_at = Some(Instant::now());
        }

        self.buffered_events.push(event);

        if is_safety || self.buffered_events.len() >= self.policy.max_events {
            FlushDecision::FlushImmediately
        } else {
            FlushDecision::Buffered
        }
    }

    /// Checks whether the batcher delay has expired.
    pub fn is_timed_out(&self) -> bool {
        if let Some(first) = self.first_event_at {
            first.elapsed().as_millis() >= self.policy.max_delay_ms as u128
        } else {
            false
        }
    }

    /// Returns the number of events currently buffered.
    pub fn len(&self) -> usize {
        self.buffered_events.len()
    }

    pub fn is_empty(&self) -> bool {
        self.buffered_events.is_empty()
    }

    /// Flushes all accumulated events into an `EventPackPlaintext`.
    pub fn flush(&mut self) -> Option<EventPackPlaintext> {
        if self.buffered_events.is_empty() {
            return None;
        }

        let mut nonce = [0u8; 16];
        OsRng.fill_bytes(&mut nonce);

        let events = std::mem::take(&mut self.buffered_events);
        self.first_event_at = None;

        Some(EventPackPlaintext {
            schema_version: 2,
            ticket_id: self.ticket_id,
            ticket_crypto_epoch: self.ticket_crypto_epoch,
            pack_nonce: nonce,
            events,
        })
    }
}
