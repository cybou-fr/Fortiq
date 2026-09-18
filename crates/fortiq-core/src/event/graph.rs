use anyhow::Result;
use std::collections::{HashMap, HashSet};

use super::heads::Heads;
use super::model::Event;
use crate::object::ObjectId;

/// In-memory causal DAG of Events.
#[derive(Debug, Clone, Default)]
pub struct EventGraph {
    events: HashMap<ObjectId, Event>,
    children: HashMap<ObjectId, Vec<ObjectId>>,
    heads: Heads,
}

impl EventGraph {
    pub fn new() -> Self {
        Self {
            events: HashMap::new(),
            children: HashMap::new(),
            heads: Heads::new(),
        }
    }

    /// Returns the number of events in the graph.
    pub fn len(&self) -> usize {
        self.events.len()
    }

    pub fn is_empty(&self) -> bool {
        self.events.is_empty()
    }

    /// Checks whether an event exists in the graph.
    pub fn contains(&self, id: &ObjectId) -> bool {
        self.events.contains_key(id)
    }

    /// Gets an event by its ObjectId.
    pub fn get(&self, id: &ObjectId) -> Option<&Event> {
        self.events.get(id)
    }

    /// Returns current heads of the DAG.
    pub fn heads(&self) -> &Heads {
        &self.heads
    }

    /// Returns heads belonging to a specific ticket, or all DAG heads if no ticket is specified.
    pub fn ticket_heads(&self, ticket_id: &str) -> Vec<ObjectId> {
        let matching_events: Vec<&Event> = self
            .events
            .values()
            .filter(|e| e.ticket_id() == Some(ticket_id))
            .collect();

        if matching_events.is_empty() {
            return Vec::new();
        }

        let mut heads = Vec::new();
        for event in &matching_events {
            let has_matching_child = self.children.get(&event.id).is_some_and(|children| {
                children.iter().any(|child_id| {
                    self.events
                        .get(child_id)
                        .is_some_and(|c| c.ticket_id() == Some(ticket_id))
                })
            });
            if !has_matching_child {
                heads.push(event.id);
            }
        }
        heads.sort();
        heads
    }

    /// Returns all event IDs belonging to a specific ticket, sorted deterministically.
    pub fn ticket_event_ids(&self, ticket_id: &str) -> Vec<ObjectId> {
        let mut ids: Vec<ObjectId> = self
            .events
            .values()
            .filter(|e| e.ticket_id() == Some(ticket_id))
            .map(|e| e.id)
            .collect();
        ids.sort();
        ids
    }

    /// Inserts an event into the graph.
    /// Insertion is idempotent: if the event is already present, this is a no-op.
    pub fn insert(&mut self, event: Event) -> Result<()> {
        if self.events.contains_key(&event.id) {
            return Ok(());
        }

        // Enforce causal Lamport monotonicity over known parents
        for parent in &event.parents {
            if let Some(parent_event) = self.events.get(parent) {
                if event.lamport <= parent_event.lamport {
                    anyhow::bail!(
                        "causal invariant violated: event {} lamport {} <= parent {} lamport {}",
                        event.id,
                        event.lamport,
                        parent,
                        parent_event.lamport
                    );
                }
            }
        }

        // Also check any existing children that arrived earlier out-of-order
        if let Some(children) = self.children.get(&event.id) {
            for child_id in children {
                if let Some(child_event) = self.events.get(child_id) {
                    if child_event.lamport <= event.lamport {
                        anyhow::bail!(
                            "causal invariant violated: child {} lamport {} <= event {} lamport {}",
                            child_id,
                            child_event.lamport,
                            event.id,
                            event.lamport
                        );
                    }
                }
            }
        }

        // Register event as child of all its parents
        for parent in &event.parents {
            self.children.entry(*parent).or_default().push(event.id);
        }

        // Any parent of this event can no longer be a head
        let mut heads_set: std::collections::BTreeSet<ObjectId> =
            self.heads.iter().copied().collect();
        for parent in &event.parents {
            heads_set.remove(parent);
        }

        // If this event does NOT have children already in the graph, it is a head
        let has_children = self
            .children
            .get(&event.id)
            .is_some_and(|children| !children.is_empty());

        if !has_children {
            heads_set.insert(event.id);
        }

        self.heads = Heads::from_set(heads_set);
        self.events.insert(event.id, event);
        Ok(())
    }

    /// Finds any parent IDs referenced by events in this graph that are missing from storage.
    pub fn missing_parents(&self) -> Vec<ObjectId> {
        let mut missing = HashSet::new();
        for event in self.events.values() {
            for parent in &event.parents {
                if !self.events.contains_key(parent) {
                    missing.insert(*parent);
                }
            }
        }
        missing.into_iter().collect()
    }

    /// Checks whether all causal parents are present in the graph.
    pub fn is_causally_complete(&self) -> bool {
        self.missing_parents().is_empty()
    }

    /// Calculates the next Lamport logical clock value for an event with the given parents.
    pub fn next_lamport(&self, parents: &[ObjectId]) -> u64 {
        let mut max_lamport = 0;
        for parent in parents {
            if let Some(parent_event) = self.events.get(parent) {
                if parent_event.lamport > max_lamport {
                    max_lamport = parent_event.lamport;
                }
            }
        }
        max_lamport + 1
    }

    /// Produces a deterministic topological sort of all events in the graph
    /// strictly respecting causal dependencies, and tie-breaking concurrent events
    /// by (lamport, timestamp, id).
    pub fn topological_sort(&self) -> Vec<&Event> {
        // Calculate in-degrees (number of known parents present in this graph)
        let mut in_degrees: HashMap<ObjectId, usize> = HashMap::new();
        for event in self.events.values() {
            let present_parents = event
                .parents
                .iter()
                .filter(|p| self.events.contains_key(p))
                .count();
            in_degrees.insert(event.id, present_parents);
        }

        #[derive(Eq, PartialEq)]
        struct SortKey<'a>(&'a Event);

        impl<'a> Ord for SortKey<'a> {
            fn cmp(&self, other: &Self) -> std::cmp::Ordering {
                self.0
                    .lamport
                    .cmp(&other.0.lamport)
                    .then_with(|| self.0.timestamp.cmp(&other.0.timestamp))
                    .then_with(|| self.0.id.cmp(&other.0.id))
            }
        }

        impl<'a> PartialOrd for SortKey<'a> {
            fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
                Some(self.cmp(other))
            }
        }

        let mut ready: std::collections::BTreeSet<SortKey> = in_degrees
            .iter()
            .filter(|&(_, &deg)| deg == 0)
            .filter_map(|(id, _)| self.events.get(id).map(SortKey))
            .collect();

        let mut result = Vec::with_capacity(self.events.len());

        while let Some(SortKey(event)) = ready.pop_first() {
            result.push(event);

            if let Some(children) = self.children.get(&event.id) {
                for child_id in children {
                    if let Some(deg) = in_degrees.get_mut(child_id) {
                        *deg = deg.saturating_sub(1);
                        if *deg == 0 {
                            if let Some(child_event) = self.events.get(child_id) {
                                ready.insert(SortKey(child_event));
                            }
                        }
                    }
                }
            }
        }

        // If any disconnected components remain, append deterministically sorted
        if result.len() < self.events.len() {
            let emitted: HashSet<ObjectId> = result.iter().map(|e| e.id).collect();
            let mut remaining: Vec<&Event> = self
                .events
                .values()
                .filter(|e| !emitted.contains(&e.id))
                .collect();
            remaining.sort_by(|a, b| {
                a.lamport
                    .cmp(&b.lamport)
                    .then_with(|| a.timestamp.cmp(&b.timestamp))
                    .then_with(|| a.id.cmp(&b.id))
            });
            result.extend(remaining);
        }

        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::event::model::EventPayload;
    use crate::object::Ed25519Signer;

    #[test]
    fn test_graph_insertion_and_topological_sort() {
        let signer = Ed25519Signer::generate();
        let mut graph = EventGraph::new();

        let p1 = EventPayload::Custom {
            name: "init".into(),
            data: vec![1],
        };
        let (e1, _) = Event::new(vec![], 100, 1, p1, &signer).unwrap();

        let p2 = EventPayload::Custom {
            name: "branch-a".into(),
            data: vec![2],
        };
        let (e2, _) = Event::new(vec![e1.id], 110, 2, p2, &signer).unwrap();

        let p3 = EventPayload::Custom {
            name: "branch-b".into(),
            data: vec![3],
        };
        let (e3, _) = Event::new(vec![e1.id], 105, 2, p3, &signer).unwrap();

        let p4 = EventPayload::Custom {
            name: "merge".into(),
            data: vec![4],
        };
        let (e4, _) = Event::new(vec![e2.id, e3.id], 120, 3, p4, &signer).unwrap();

        // Insert in scrambled order
        graph.insert(e3.clone()).unwrap();
        graph.insert(e4.clone()).unwrap();
        graph.insert(e1.clone()).unwrap();
        graph.insert(e2.clone()).unwrap();

        assert_eq!(graph.len(), 4);
        assert_eq!(graph.heads().to_vec(), vec![e4.id]);

        let sorted = graph.topological_sort();
        assert_eq!(sorted.len(), 4);
        assert_eq!(sorted[0].id, e1.id);
        assert_eq!(sorted[3].id, e4.id);
        // Both e2 and e3 are between e1 and e4
        assert!(sorted[1].id == e2.id || sorted[1].id == e3.id);
        assert!(sorted[2].id == e2.id || sorted[2].id == e3.id);
    }

    #[test]
    fn test_graph_causal_lamport_violation_rejected() {
        let signer = Ed25519Signer::generate();
        let mut graph = EventGraph::new();

        let p1 = EventPayload::Custom {
            name: "e1".into(),
            data: vec![1],
        };
        let (e1, _) = Event::new(vec![], 100, 5, p1, &signer).unwrap();
        graph.insert(e1.clone()).unwrap();

        // Child event with lamport <= parent lamport (5 <= 5) must be rejected
        let p2 = EventPayload::Custom {
            name: "e2-bad".into(),
            data: vec![2],
        };
        let (e2_bad, _) = Event::new(vec![e1.id], 110, 5, p2, &signer).unwrap();
        let res = graph.insert(e2_bad);
        assert!(res.is_err());
        assert!(res
            .unwrap_err()
            .to_string()
            .contains("causal invariant violated"));
    }

    #[test]
    fn test_ticket_heads_and_filtering() {
        let signer = Ed25519Signer::generate();
        let mut graph = EventGraph::new();

        let t1_create = EventPayload::Ticket(crate::ticket::TicketEvent::Created {
            ticket_id: "T-1".into(),
            title: "Test 1".into(),
            description: "Desc 1".into(),
            priority: crate::ticket::TicketPriority::Normal,
            client_peer_id: "peer-1".into(),
        });
        let (e1, _) = Event::new(vec![], 100, 1, t1_create, &signer).unwrap();

        let t2_create = EventPayload::Ticket(crate::ticket::TicketEvent::Created {
            ticket_id: "T-2".into(),
            title: "Test 2".into(),
            description: "Desc 2".into(),
            priority: crate::ticket::TicketPriority::High,
            client_peer_id: "peer-2".into(),
        });
        let (e2, _) = Event::new(vec![], 101, 1, t2_create, &signer).unwrap();

        let t1_msg = EventPayload::Ticket(crate::ticket::TicketEvent::ChatMessageAdded {
            ticket_id: "T-1".into(),
            message_id: "m-1".into(),
            sender_peer_id: "peer-1".into(),
            body: "hello".into(),
            timestamp: 102,
        });
        let (e3, _) = Event::new(vec![e1.id], 102, 2, t1_msg, &signer).unwrap();

        graph.insert(e1.clone()).unwrap();
        graph.insert(e2.clone()).unwrap();
        graph.insert(e3.clone()).unwrap();

        // T-1 head should be e3 (e1 was superseded by e3)
        assert_eq!(graph.ticket_heads("T-1"), vec![e3.id]);
        // T-2 head should be e2
        assert_eq!(graph.ticket_heads("T-2"), vec![e2.id]);
        // Unknown ticket should return empty
        assert_eq!(graph.ticket_heads("T-999"), Vec::<ObjectId>::new());

        let t1_ids = graph.ticket_event_ids("T-1");
        assert_eq!(t1_ids.len(), 2);
        assert!(t1_ids.contains(&e1.id));
        assert!(t1_ids.contains(&e3.id));
    }
}
