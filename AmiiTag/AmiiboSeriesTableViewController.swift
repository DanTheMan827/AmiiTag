//
//  AmiiboSeriesTableViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/12/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

class AmiiboSeriesTableViewController: UITableViewController {
    var amiiboSeries: [Dictionary<String, String>.Element] = []
    
    // MARK: UIViewController
    override func viewDidLoad() {
        super.viewDidLoad()
        
        amiiboSeries = AmiiboDatabase.database.AmiiboSeries.sorted { $0.1 < $1.1 }
    }
    
    // MARK: UITableViewDataSource
    override func numberOfSections(in tableView: UITableView) -> Int {
        return 1
    }
    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        return amiiboSeries.count
    }
    override func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let index = (indexPath as NSIndexPath).row
        let basicCell = UITableViewCell(frame: CGRect.zero)
        basicCell.textLabel?.text = amiiboSeries[index].value
        return basicCell
    }
    
    // MARK: UITableViewDelegate
    override func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "AmiiboCharacters") as? AmiiboCharactersTableViewController else {
            return
        }
        view.seriesFilter = String(amiiboSeries[indexPath.row].key.suffix(2))
        view.title = amiiboSeries[indexPath.row].value
        (self.view as? UITableView)?.deselectRow(at: indexPath, animated: true)
        self.present(UINavigationController(rootViewController: view), animated: true, completion: nil)
    }
}
