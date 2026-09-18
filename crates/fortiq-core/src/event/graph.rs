use std::collections::{HashMap, HashSet};
use anyhow::Result;

use super::event::Event;
use super::heads::Heads;
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

    /// Inserts an event into the graph.
    /// Insertion is idempotent: if the event is already present, this is a no-op.
    pub fn insert(&mut self, event: Event) -> Result<()> {
        if self.events.contains_key(&event.id) {
            return Ok(());
        }

        // Register event as child of all its parents
        for parent in &event.parents {
            self.children.entry(*parent).or_default().push(event.id);
        }

        // Any parent of this event can no longer be a head
        let mut heads_set: std::collections::BTreeSet<ObjectId> = self.heads.iter().copied().collect();
        for parent in &event.parents {
            heads_set.remove(parent);
        }

        // If this event does NOT have children already in the graph, it is a head
        let has_children = self
            .children
            .get(&event.id)
            .map_or(false, |children| !children.is_empty());

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

    /// Produces a deterministic topological sort of all events in the graph.
    /// Because child.lamport > parent.lamport for all causal links,
    /// ordering by (lamport, timestamp, id) guarantees a valid and deterministic causal order.
    pub fn topological_sort(&self) -> Vec<&Event> {
        let mut sorted: Vec<&Event> = self.events.values().collect();
        sorted.sort_by(|a, b| {
            a.lamport
                .cmp(&b.lamport)
                .then_with(|| a.timestamp.cmp(&b.timestamp))
                .then_with(|| a.id.cmp(&b.id))
        });
        sorted
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::event::event::EventPayload;
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
}
